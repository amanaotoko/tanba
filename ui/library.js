'use strict';

// Отбор работает как запрос: набор тегов, режим И/ИЛИ и подстрока в имени.
// Порядок тегов ничего не значит, иерархии между ними нет.
// dates: какую дату мерить, created | modified | any (любая из двух).
// Дата не тег: она непрерывна, поэтому отбирается диапазоном.
// exts: выбранные форматы. Ведут себя как обычная группа тегов
// и слушаются того же переключателя И/ИЛИ.
// cat: в каком каталоге мы сейчас. Это область, а не условие отбора:
// она всегда И, независимо от переключателя. Внутри видны прямые дети.
// show: что показывать в выдаче. Не формат и не тег, а вид объекта,
// поэтому отдельная группа. Хотя бы один всегда выбран.
const pick = { tags: [], exts: [], show: ['files', 'catalogs'],
               mode: 'and', text: '', from: '', to: '', dates: 'any', cat: null };
let catInfo = null;   // имя, путь наверх и дерево текущего каталога

// Свёрнутые группы переживают перезапуск: панель длинная, и каждый раз
// сворачивать одно и то же было бы издевательством.
const collapsed = new Set(JSON.parse(localStorage.getItem('tanba-collapsed') || '[]'));
let dateOpen = false;

function toggleGroup(name) {
  collapsed.has(name) ? collapsed.delete(name) : collapsed.add(name);
  localStorage.setItem('tanba-collapsed', JSON.stringify([...collapsed]));
  renderPanel();
}

// Заголовок группы кликабельный, шеврон показывает состояние.
// Ключ сворачивания отдельно от заголовка: у групп тегов имя приходит из базы
// и годится как ключ, а у своих групп заголовок переводится, и свёрнутое
// не должно разворачиваться от смены языка.
const head = (name, extra = '', key = name) =>
  `<h3 data-collapse="${esc(key)}">${esc(name)}${extra}` +
  `<svg class="ic chev"><use href="#i-chev"></use></svg></h3>`;

const grp = (name, inner, extra = '', key = name) =>
  `<div class="group${collapsed.has(key) ? ' closed' : ''}">${head(name, extra, key)}${inner}</div>`;
const PAGE = 200;

let res = { files: [], total: 0, bytes: 0 };
let facets = { total: 0, root: '', groups: [] };
let facetsReady = false;   // ответ сервера уже приезжал, пустоте можно верить
let saved = [];
let tagIx = {};   // id -> { tag, group }

const $ = id => document.getElementById(id);

const jget = async url => {
  const r = await fetch(url);
  if (!r.ok) throw new Error(await r.text());
  return r.json();
};
const jsend = async (url, method, body) => {
  const r = await fetch(url, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
};

function params(extra) {
  const p = new URLSearchParams();
  if (pick.tags.length) p.set('tags', pick.tags.join(','));
  if (pick.exts.length) p.set('ext', pick.exts.join(','));
  p.set('mode', pick.mode);
  if (pick.text) p.set('q', pick.text);
  if (pick.from) p.set('from', pick.from);
  if (pick.to) p.set('to', pick.to);
  if (pick.from || pick.to) p.set('dates', pick.dates);
  if (pick.cat) p.set('cat', pick.cat);
  if (pick.show.length === 1) p.set('show', pick.show[0]);
  for (const k in extra || {}) p.set(k, extra[k]);
  return p.toString();
}

// ── Загрузка ───────────────────────────────────────────────────────────

async function load(append) {
  const offset = append ? res.files.length : 0;
  try {
    const jobs = [jget('/api/library?' + params({ limit: PAGE, offset }))];
    if (!append) jobs.push(jget('/api/library/facets?' + params()));
    const [page, fac] = await Promise.all(jobs);

    res = append ? Object.assign({}, page, { files: res.files.concat(page.files) }) : page;
    if (fac) { facets = fac; facetsReady = true; sortFacets(); indexTags(); }
    render();
  } catch (e) {
    toast(t('toast.filterFailed', { error: e.message }), 'err');
  }
}

async function loadSaved() {
  try { saved = await jget('/api/library/saved'); renderPanel(); }
  catch { /* сохранённых может не быть, это не повод шуметь */ }
}

// База сортирует байтами: вся латиница идёт раньше всей кириллицы, и список
// тегов на двух языках выглядит перемешанным. Раскладываем на своей стороне,
// как только пришёл ответ, чтобы дальше все списки шли уже в этом порядке.
// Порядок групп и видов объектов не трогаем: он задан не именем, а смыслом.
function sortFacets() {
  for (const g of facets.groups) g.tags.sort((a, b) => I18N.byName(a.name, b.name));
  if (facets.formats && facets.formats.items)
    facets.formats.items.sort((a, b) => I18N.byName(a.name, b.name));
}

function indexTags() {
  tagIx = {};
  for (const g of facets.groups)
    for (const t of g.tags) tagIx[t.id] = { tag: t, group: g };
}

// ── Отрисовка ──────────────────────────────────────────────────────────

function render() {
  renderScope();
  renderPanel();
  renderResults();

  $('stRoot').textContent = facets.root || '';
  // Строка состояния знает про выделение, а оно живёт в actions.js,
  // который грузится следующим.
  if (typeof updateStatus === 'function') updateStatus();
  for (const b of $('mode').children) b.classList.toggle('on', b.dataset.mode === pick.mode);

  // Панель одинаковая везде. Раньше внутри каталога отбор прятался целиком,
  // но панель, меняющая состав при каждом входе в папку, заставляет заново
  // искать глазами кнопку, которая была тут секунду назад. Отбор внутри
  // каталога работает: запрос и каталог складываются на стороне базы.

  // Сброс появляется только когда есть что сбрасывать: иначе это кнопка,
  // которая ничего не делает, и глаз к ней привыкает как к украшению.
  // Сброс не исчезает, а гаснет: пропадающая кнопка меняет ширину строки,
  // и всё остальное дёргается при каждом теге.
  const dirty = pick.tags.length || pick.exts.length || pick.text ||
                pick.from || pick.to || pick.show.length === 1;
  $('reset').disabled = !dirty;
}

// Вход в каталог. У каталогов иерархия настоящая, поэтому путь наверх честный,
// в отличие от тегов, где крошки были бы враньём.
async function enterCatalog(id) {
  pick.cat = id || null;
  catInfo = null;
  if (id) {
    try { catInfo = await jget('/api/catalogs/' + id); }
    catch { pick.cat = null; }
  }
  load();
}

// Что сейчас отобрано: путь по каталогам, снятые теги и форматы.
function renderScope() {
  const box = $('scope');

  // Начинается со значка раздела, как адресная строка проводника. Внутри
  // каталога он же и выход наверх: слово «Библиотека» тут лишнее, значок
  // говорит то же самое и не занимает полстроки.
  const path = catInfo && catInfo.path || [];
  const crumbs = `
    <span class="pill pill-cat pill-root"${pick.cat ? ' data-goto=""' : ''}
          title="${pick.cat ? t('lib.scope.up') : t('lib.scope.root')}">
      <svg class="ic"><use href="#i-lib"></use></svg>
    </span>`
    + path.map(p => `
    <span class="crumb-sep"><svg class="ic"><use href="#i-crumb"></use></svg></span>
    <span class="pill pill-cat${p.id === pick.cat ? ' pill-here' : ''}" data-goto="${p.id}"
          title="${t('lib.scope.goto', { name: esc(p.name) })}">${esc(p.name)}</span>`).join('');

  box.innerHTML = crumbs + pick.tags.map(id => `
    <span class="pill">${esc(name(id))}<button class="x" data-drop="${id}" title="${t('scope.removeTag')}">
      <svg class="ic"><use href="#i-x"></use></svg></button></span>`).join('')
    + pick.exts.map(e => `
    <span class="pill">${esc(e)}<button class="x" data-dropext="${esc(e)}" title="${t('scope.removeFormat')}">
      <svg class="ic"><use href="#i-x"></use></svg></button></span>`).join('');

  box.querySelectorAll('[data-drop]').forEach(el => {
    el.onclick = () => toggleTag(+el.dataset.drop);
  });
  box.querySelectorAll('[data-dropext]').forEach(el => {
    el.onclick = () => toggleExt(el.dataset.dropext);
  });
  box.querySelectorAll('[data-goto]').forEach(el => {
    el.onclick = e => { e.stopPropagation(); enterCatalog(+el.dataset.goto || null); };
  });
  fadeEdges();
}

// Край растворяется только когда есть что прокручивать, и с той стороны,
// где ещё осталось содержимое.
function fadeEdges() {
  const box = $('scope');
  const more = box.scrollWidth - box.clientWidth;
  box.classList.toggle('fade-r', more > 1 && box.scrollLeft < more - 1);
  box.classList.toggle('fade-l', more > 1 && box.scrollLeft > 1);
}

// Внутри каталога панель отдаётся под дерево целиком. Два честных режима:
// снаружи ищешь, внутри ходишь. Искать внутри работы из десятка файлов нечего.
function renderTree(node, cur, depth = 0) {
  const on = node.id === cur;
  return `
    <button class="tag tag-tree${on ? ' tag-here' : ''}" data-goto="${node.id}"
            style="--depth:${depth}">
      <svg class="ic"><use href="#i-folder"></use></svg>
      <span class="lbl">${esc(node.name)}</span>
    </button>` + (node.children || []).map(k => renderTree(k, cur, depth + 1)).join('');
}

function renderPanel() {
  if (pick.cat) {
    $('panel').innerHTML = catInfo && catInfo.tree
      ? `<div class="group"><h3>${t('lib.panel.catalog')}</h3>${renderTree(catInfo.tree, pick.cat)}</div>`
      : '';
    $('panel').querySelectorAll('[data-goto]').forEach(el => {
      el.onclick = () => enterCatalog(+el.dataset.goto);
    });
    return;
  }

  const savedBlock = saved.length ? `
    <div class="saved">
      <h3>${t('lib.saved.title')}</h3>
      ${saved.map(s => `
        <div class="saved-row">
          <button class="nm" data-apply="${s.id}" title="${esc(s.name)}">${esc(s.name)}</button>
          <button class="del" data-del="${s.id}" title="${t('lib.saved.del')}">
            <svg class="ic"><use href="#i-trash"></use></svg>
          </button>
        </div>`).join('')}
    </div>` : '';

  // Все группы устроены одинаково. Отличается только группа
  // с единственным выбором: там радиокнопки вместо флажков.
  const groups = facets.groups.map(g => grp(g.name, `
      ${nest(g.tags).map(({ t, d }) => {
        const on = pick.tags.includes(t.id);
        return `
        <button class="tag${t.count ? '' : ' tag-zero'}" data-tag="${t.id}"
                style="--depth:${d}">
          <span class="box${on ? ' on' : ''}${g.isMulti ? '' : ' radio'}">
            <svg class="ic"><use href="#i-check"></use></svg>
          </span>
          <span class="lbl">${esc(t.name)}</span>
          <span class="n mono">${t.count ? num(t.count) : ''}</span>
        </button>`;
      }).join('')}`,
      g.isMulti ? '' : ` <span class="single">${t('tag.group.single')}</span>`)).join('');

  // Формат рисуется тем же блоком, что и группы тегов: снаружи он от них
  // ничем не отличается, только значения берутся из расширений файлов.
  const fmt = facets.formats;
  const formatBlock = fmt && fmt.items && fmt.items.length ? grp(fmt.name, `
      ${fmt.items.map(it => {
        const on = pick.exts.includes(it.ext);
        return `
        <button class="tag${it.count ? '' : ' tag-zero'}" data-ext="${esc(it.ext)}">
          <span class="box${on ? ' on' : ''}">
            <svg class="ic"><use href="#i-check"></use></svg>
          </span>
          <span class="lbl">${esc(it.name)}</span>
          <span class="n mono">${it.count ? num(it.count) : ''}</span>
        </button>`;
      }).join('')}`) : '';

  // Вид объекта идёт первой группой: она отвечает на вопрос «что вообще
  // показывать», а формат и теги уточняют уже внутри этого.
  const k = facets.kinds;
  const showBlock = k ? grp(k.name, `
      ${k.items.map(it => {
        const on = pick.show.includes(it.key);
        return `
        <button class="tag${it.count ? '' : ' tag-zero'}" data-show="${it.key}">
          <span class="box${on ? ' on' : ''}">
            <svg class="ic"><use href="#i-check"></use></svg>
          </span>
          <span class="lbl">${esc(it.name)}</span>
          <span class="n mono">${it.count ? num(it.count) : ''}</span>
        </button>`;
      }).join('')}`) : '';

  // Дата не тег: она непрерывна, поэтому диапазон, а не список значений.
  // Выпадающий список взят из кита, раздел «Поиск и фильтры».
  const DATES = { any: t('lib.date.any'), created: t('lib.date.created'), modified: t('lib.date.modified') };
  const dateBlock = grp(t('lib.date.group'), `
      <div class="dd">
        <button class="dd-btn" id="ddDate">
          <svg class="ic"><use href="#i-calendar"></use></svg>
          <span>${DATES[pick.dates]}</span>
          <svg class="ic chev"><use href="#i-chev"></use></svg>
        </button>
        ${dateOpen ? `<div class="dd-menu">${Object.keys(DATES).map(key => `
          <button data-dates="${key}">${DATES[key]}
            ${key === pick.dates ? '<svg class="ic"><use href="#i-check"></use></svg>' : ''}
          </button>`).join('')}</div>` : ''}
      </div>
      <div class="range">
        <input type="date" id="from" value="${pick.from}" title="${t('lib.date.from')}">
        <span class="dash">${t('lib.date.dash')}</span>
        <input type="date" id="to" value="${pick.to}" title="${t('lib.date.to')}">
      </div>`, '', 'date');

  // Тегов ещё нет: панель без единой группы выглядит сломанной, поэтому
  // говорим то же самое, что и разбор, а не оставляем пустое место.
  // Но только когда фасеты действительно приехали: сохранённые отборы
  // приходят раньше них, и без этой проверки панель на мгновение мигала
  // ложным «Тегов пока нет» при каждом заходе.
  const noTags = facets.groups.length || !facetsReady ? '' : `
    <div class="group">
      <h3>${t('tags.none.title')}</h3>
      <p class="dim" style="margin:0">${t('tags.none.note')}</p>
    </div>`;

  $('panel').innerHTML = savedBlock + showBlock + formatBlock + dateBlock + groups + noTags;

  $('panel').querySelectorAll('[data-collapse]').forEach(el => {
    el.onclick = () => toggleGroup(el.dataset.collapse);
  });
  $('ddDate').onclick = e => { e.stopPropagation(); dateOpen = !dateOpen; renderPanel(); };

  // Меню вынуто из потока, поэтому место ему считаем от кнопки.
  // Если снизу не помещается, разворачиваем вверх.
  const menu = $('panel').querySelector('.dd-menu');
  if (menu) {
    const b = $('ddDate').getBoundingClientRect();
    menu.style.left = b.left + 'px';
    menu.style.width = b.width + 'px';
    const below = innerHeight - b.bottom;
    if (below < menu.offsetHeight + 12) menu.style.top = (b.top - menu.offsetHeight - 6) + 'px';
    else menu.style.top = (b.bottom + 6) + 'px';
  }
  $('panel').querySelectorAll('[data-dates]').forEach(el => {
    el.onclick = () => {
      pick.dates = el.dataset.dates;
      dateOpen = false;
      (pick.from || pick.to) ? load() : renderPanel();
    };
  });
  for (const id of ['from', 'to']) $(id).onchange = () => { pick[id] = $(id).value; load(); };

  $('panel').querySelectorAll('[data-show]').forEach(el => {
    el.onclick = () => toggleShow(el.dataset.show);
  });

  $('panel').querySelectorAll('[data-tag]').forEach(el => {
    el.onclick = () => toggleTag(+el.dataset.tag);
  });
  $('panel').querySelectorAll('[data-ext]').forEach(el => {
    el.onclick = () => toggleExt(el.dataset.ext);
  });
  $('panel').querySelectorAll('[data-apply]').forEach(el => {
    el.onclick = () => applySaved(+el.dataset.apply);
  });
  $('panel').querySelectorAll('[data-del]').forEach(el => {
    el.onclick = () => dropSaved(+el.dataset.del);
  });
}

/// Что сейчас нарисовано. Тот же приём, что у разбора: пока набор карточек
/// не изменился, сетку не пересобираем. Пересборка через innerHTML заставляла
/// каждую картинку проявляться заново и сбрасывала прокрутку, а сюда попадает
/// каждое обновление по возврату фокуса в окно.
let shownKey = '';

function renderResults() {
  const box = $('results');

  if (!res.files.length) {
    shownKey = '';
    // Пусто по-разному: библиотека и правда пуста или отбор ничего не поймал.
    const filtered = pick.tags.length || pick.exts.length || pick.text || pick.from || pick.to;
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-grid"></use></svg></div>
        <h2>${filtered ? t('lib.empty.noMatch.title') : t('lib.empty.title')}</h2>
        <p>${filtered
          ? (pick.from || pick.to
            ? t('lib.empty.hintDates')
            : t('lib.empty.hintTags'))
          : t('lib.empty.body')}</p>
      </div>`;
    return;
  }

  const key = JSON.stringify([pick, res.total,
    res.files.map(f => [f.id, f.name, f.modifiedAt, f.movedTo, f.count])]);
  if (key === shownKey && box.querySelector('.grid')) return;
  shownKey = key;

  box.innerHTML = `<div class="grid">` + res.files.map(f => `
    <div class="card${f.kind === 'catalog' ? ' card-cat' : ''}${f.movedTo ? ' card-away' : ''}"
         data-id="${f.id}" data-kind="${f.kind || 'file'}" draggable="true"
         title="${tip(f)}">
      <div class="thumb">
        ${f.kind === 'catalog'
          ? `<svg class="ic"><use href="#i-folder"></use></svg>`
          : ftypeIcon(f.ext)}
        ${f.kind === 'catalog' ? '' : ftypeBadge(f.ext)}
        ${f.kind === 'catalog' ? ''
          : `<img loading="lazy" src="/api/thumb/${f.id}${f.modifiedAt ? '?v=' + f.modifiedAt : ''}" alt="" onload="this.classList.add('ok')" onerror="this.remove()">`}
        ${f.movedTo ? `<span class="away" title="${t('card.movedAway', { path: esc(f.movedTo) })}">${t('card.away')}</span>` : ''}
      </div>
      <div class="name">${esc(f.name)}</div>
    </div>`).join('') + `</div>`
    + (res.files.length < res.total
      ? `<div class="more"><button class="btn" id="more">${t('lib.more', { n: num(res.total - res.files.length) })}</button></div>`
      : '');

  // Двойной клик по каталогу входит внутрь, по файлу открывает сам файл,
  // как в проводнике. Показать файл в проводнике осталось в правом меню:
  // это отдельное действие, и им пользуются заметно реже, чем открывают.
  box.querySelectorAll('.card').forEach(el => {
    el.ondblclick = () => el.dataset.kind === 'catalog'
      ? enterCatalog(+el.dataset.id)
      : openFile(+el.dataset.id);
  });
  box.querySelectorAll('[data-add]').forEach(el => {
    el.onclick = e => { e.stopPropagation(); toggleTag(+el.dataset.add); };
    el.ondblclick = e => e.stopPropagation();   // двойной клик по тегу не должен открывать файл
  });
  if ($('more')) $('more').onclick = () => load(true);
}

//// Подсказка карточки. Теги переехали сюда с самой карточки: там они
/// переносились и рвали высоту рядов. Перевод строки в подсказке задаётся
/// мнемоникой, обычный \n в значении атрибута не сработает.
function tip(f) {
  const tags = (f.tags || []).map(id => name(id)).filter(Boolean).sort(I18N.byName);
  const parts = [f.name];
  if (tags.length) parts.push(tags.join(', '));
  if (f.movedTo) parts.push(t('card.movedAway', { path: f.movedTo }));
  return esc(parts.join('\n')).replace(/\n/g, '&#10;');
}

// Вложенность внутри группы: ребёнок идёт сразу за своим родителем.
function nest(tags) {
  const ids = new Set(tags.map(t => t.id));
  const kids = {}, roots = [], out = [];
  for (const t of tags) {
    if (t.parentId && ids.has(t.parentId)) (kids[t.parentId] = kids[t.parentId] || []).push(t);
    else roots.push(t);
  }
  const walk = (t, d) => { out.push({ t, d }); (kids[t.id] || []).forEach(k => walk(k, d + 1)); };
  roots.forEach(t => walk(t, 0));
  return out;
}

// ── Действия ───────────────────────────────────────────────────────────

function toggleTag(id) {
  if (pick.tags.includes(id)) {
    pick.tags = pick.tags.filter(x => x !== id);
  } else {
    const g = tagIx[id] && tagIx[id].group;
    // В группе с единственным выбором новый тег вытесняет прежний.
    if (g && !g.isMulti) pick.tags = pick.tags.filter(x => !(tagIx[x] && tagIx[x].group.id === g.id));
    pick.tags.push(id);
  }
  load();
}

// Снять последнюю галочку нельзя: пустая выдача никому не нужна.
// Вместо этого включается вторая, то есть ведёт себя как радиокнопка,
// но обе держать тоже можно.
// Клик мимо и Escape закрывают меню: висящее поверх окно должно
// закрываться тем же жестом, что и любое другое.
document.addEventListener('click', () => {
  if (!dateOpen) return;
  dateOpen = false;
  renderPanel();
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && dateOpen) { dateOpen = false; renderPanel(); }
});

function toggleShow(key) {
  const other = key === 'files' ? 'catalogs' : 'files';
  if (!pick.show.includes(key)) pick.show = [...pick.show, key];
  else if (pick.show.length > 1) pick.show = pick.show.filter(x => x !== key);
  else pick.show = [other];
  load();
}

function toggleExt(ext) {
  pick.exts = pick.exts.includes(ext)
    ? pick.exts.filter(x => x !== ext)
    : [...pick.exts, ext];
  load();
}

$('add').onclick = e => { e.stopPropagation(); openPop($('add')); };

// Колесо крутит поле отбора вбок: ползунка нет, а прокрутка нужна.
$('scope').addEventListener('wheel', e => {
  const box = $('scope');
  if (box.scrollWidth <= box.clientWidth) return;
  e.preventDefault();
  box.scrollLeft += e.deltaY || e.deltaX;
  fadeEdges();
}, { passive: false });
$('scope').addEventListener('scroll', fadeEdges);
addEventListener('resize', fadeEdges);

$('mode').onclick = e => {
  const b = e.target.closest('[data-mode]');
  if (!b || b.dataset.mode === pick.mode) return;
  pick.mode = b.dataset.mode;
  load();
};

$('reset').onclick = () => {
  pick.tags = [];
  pick.exts = [];
  pick.mode = 'and';
  pick.text = '';
  pick.from = pick.to = '';
  pick.dates = 'any';
  pick.cat = null;
  catInfo = null;
  $('q').value = '';
  // Поля дат живут в панели и перерисуются сами вместе с ней.
  load();
};

let findTimer;
$('q').oninput = () => {
  pick.text = $('q').value.trim();
  clearTimeout(findTimer);
  findTimer = setTimeout(() => load(), 250);
};

async function reveal(id) {
  try { await jsend('/api/library/reveal', 'POST', { id }); }
  catch (e) { toast(t('toast.openFailed', { error: e.message }), 'err'); }
}

/// Открыть файл тем, чем его открывает система.
async function openFile(id) {
  try { await jsend('/api/files/open', 'POST', { id }); }
  catch (e) { toast(t('toast.openFailed', { error: e.message }), 'err'); }
}

// ── Сохранённые отборы ─────────────────────────────────────────────────
// Кнопка сохранения убрана из шапки. Уже сохранённые отборы применяются
// и удаляются из панели, но завести новый из интерфейса сейчас нельзя.

function applySaved(id) {
  const s = saved.find(x => x.id === id);
  if (!s) return;
  pick.mode = s.mode === 'or' ? 'or' : 'and';
  pick.tags = (s.tagIds || []).filter(t => tagIx[t]);
  pick.exts = s.exts || [];
  load();
}

async function dropSaved(id) {
  try {
    await jsend('/api/library/saved/' + id, 'DELETE');
    await loadSaved();
  } catch (e) {
    toast(t('toast.deleteFailed', { error: e.message }), 'err');
  }
}

// ── Выбор тега для строки отбора ───────────────────────────────────────

let popRows = [], popCur = 0;

function openPop(anchor) {
  const r = anchor.getBoundingClientRect();
  const pop = $('pop');
  pop.hidden = false;
  pop.style.left = Math.min(r.left, innerWidth - 276) + 'px';
  pop.style.top = Math.min(r.bottom + 6, innerHeight - 320) + 'px';
  $('popQ').value = '';
  fillPop();
  $('popQ').focus();
}

function closePop() { $('pop').hidden = true; }

function fillPop() {
  const s = $('popQ').value.trim().toLowerCase();
  popRows = [];
  for (const g of facets.groups)
    for (const t of g.tags)
      if (!pick.tags.includes(t.id) && (!s || t.name.toLowerCase().includes(s)))
        popRows.push({ t, g });
  popCur = 0;

  $('popList').innerHTML = popRows.length
    ? popRows.map((r, i) => `
      <button class="pop-row${i === popCur ? ' cur' : ''}" data-p="${r.t.id}">
        <span class="nm">${esc(r.t.name)}</span>
        <span class="gr">${esc(r.g.name)}</span>
      </button>`).join('')
    : `<div class="pop-none">${t('common.tagNotFound')}</div>`;

  $('popList').querySelectorAll('[data-p]').forEach(el => {
    el.onclick = () => { closePop(); toggleTag(+el.dataset.p); };
  });
}

$('popQ').oninput = fillPop;
$('popQ').onkeydown = e => {
  if (e.key === 'Escape') { closePop(); return; }
  if (e.key === 'Enter') {
    if (popRows[popCur]) { closePop(); toggleTag(popRows[popCur].t.id); }
    return;
  }
  if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
  e.preventDefault();
  popCur = Math.max(0, Math.min(popRows.length - 1, popCur + (e.key === 'ArrowDown' ? 1 : -1)));
  const rows = $('popList').children;
  for (let i = 0; i < rows.length; i++) rows[i].classList.toggle('cur', i === popCur);
  if (rows[popCur]) rows[popCur].scrollIntoView({ block: 'nearest' });
};

document.addEventListener('mousedown', e => {
  if (!$('pop').hidden && !$('pop').contains(e.target) && e.target.id !== 'add') closePop();
});

// Проверяем код клавиши, а не букву: в русской раскладке оттуда приходит «л»,
// и сочетание молча ничего не делало. То же самое с CapsLock, см. picking.js.
document.addEventListener('keydown', e => {
  if (e.ctrlKey && e.code === 'KeyK') { e.preventDefault(); $('q').focus(); $('q').select(); }
  if (e.key === 'Escape' && document.activeElement === $('q')) { $('q').blur(); }
});

// ── Мелочи ─────────────────────────────────────────────────────────────

let toastTimer;
function toast(text, cls = '') {
  const el = $('toast');
  el.textContent = text;
  el.className = 'toast show ' + cls;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.className = 'toast ' + cls, 3200);
}

const name = id => (tagIx[id] ? tagIx[id].tag.name : '?');
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
// Разделитель тысяч у языков свой, поэтому число форматирует язык, а не мы.
const num = I18N.num;

function fmtSize(b) {
  if (!b) return '';
  const u = [t('units.b'), t('units.kb'), t('units.mb'), t('units.gb'), t('units.tb')];
  let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  // Дробная часть тоже за языком: у русского там запятая, у английского точка.
  return num(i === 0 ? b : +b.toFixed(b < 10 ? 1 : 0)) + ' ' + u[i];
}

// Разметку страницы заполняем здесь: словарь и механика подключены выше.
// Шапка окна заполняет себя сама, она собирается кодом, см. chrome.js.
I18N.apply();

// Быстрая копия языка могла разойтись с настоящей настройкой: её выбирают
// в мастере, а у того своё окно и свой localStorage. Сверяемся и, если
// разошлись, перерисовываемся уже на верном языке.
I18N.sync(() => load());

// ── Память страницы ─────────────────────────────────────────────────────
// Вкладки это настоящие переходы, и раньше каждый уход из библиотеки
// стирал всё: отбор, режим И/ИЛИ, даты, открытый каталог и прокрутку.
// Вернулся за файлом, который только что нашёл, а искать заново.
// Теперь отбор и прокрутка живут в sessionStorage: он умирает вместе
// с окном, поэтому каждый запуск программы всё равно начинается с чистого
// листа, а вот хождение по вкладкам внутри одного сеанса ничего не теряет.
// Выделение не запоминаем намеренно: оно про текущее действие, и кнопки
// действий не должны гореть от выбора, сделанного полчаса назад.

const MEMORY = 'tanba-lib';
let wakeScroll = 0;

try {
  const m = JSON.parse(sessionStorage.getItem(MEMORY) || 'null');
  if (m && m.pick) {
    Object.assign(pick, m.pick);
    wakeScroll = m.scroll || 0;
    // Поле поиска живёт в разметке и само из pick ничего не читает:
    // остальную панель рисует render, а этому надо помочь.
    if (pick.text) $('q').value = pick.text;

    // Первый кадр из снимка прошлого визита: сетка, панель и счётчики
    // встают сразу, а не вскакивают после ответа сервера. Свежие данные
    // придут следом, и одинаковые сетка не перерисовывает.
    if (m.snap && m.snap.res) {
      res = m.snap.res;
      facets = m.snap.facets || facets;
      saved = m.snap.saved || [];
      catInfo = m.snap.catInfo || null;
      facetsReady = true;
      sortFacets(); indexTags(); render();
      if (wakeScroll) $('results').scrollTop = wakeScroll;
    }
  }
} catch (e) { /* памяти нет, начинаем с чистого */ }

function remember() {
  try {
    sessionStorage.setItem(MEMORY, JSON.stringify({
      pick,
      scroll: $('results').scrollTop,
      snap: { res, facets, saved, catInfo },
    }));
  } catch (e) { /* нечем хранить, переживём */ }
}

// pagehide, а не beforeunload: он срабатывает и там, где документ уходит
// в кэш навигации, и не мешает этому кэшу работать.
addEventListener('pagehide', remember);

// Открытый каталог надо поднять целиком, как это делает enterCatalog:
// без catInfo строка пути и дерево в панели остались бы пустыми.
if (pick.cat) {
  enterCatalog(pick.cat);
  loadSaved();
} else {
  loadSaved();
  load();
}

// Прокрутку возвращаем после первой отрисовки: раньше некуда.
const wake = new MutationObserver(() => {
  if (!$('results').childElementCount) return;
  wake.disconnect();
  if (wakeScroll) $('results').scrollTop = wakeScroll;
});
wake.observe($('results'), { childList: true });

// Библиотека пополняется на экране разбора, обновляемся при возврате в окно.
addEventListener('focus', () => load());
