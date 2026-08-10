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
const head = (name, extra = '') =>
  `<h3 data-collapse="${esc(name)}">${esc(name)}${extra}` +
  `<svg class="ic chev"><use href="#i-chev"></use></svg></h3>`;

const grp = (name, inner, extra = '') =>
  `<div class="group${collapsed.has(name) ? ' closed' : ''}">${head(name, extra)}${inner}</div>`;
const PAGE = 200;

let res = { files: [], total: 0, bytes: 0 };
let facets = { total: 0, root: '', groups: [] };
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
    if (fac) { facets = fac; indexTags(); }
    render();
  } catch (e) {
    toast('Не удалось получить отбор: ' + e.message, 'err');
  }
}

async function loadSaved() {
  try { saved = await jget('/api/library/saved'); renderPanel(); }
  catch { /* сохранённых может не быть, это не повод шуметь */ }
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
          title="${pick.cat ? 'Выйти в библиотеку' : 'Библиотека'}">
      <svg class="ic"><use href="#i-lib"></use></svg>
    </span>`
    + path.map(p => `
    <span class="crumb-sep"><svg class="ic"><use href="#i-crumb"></use></svg></span>
    <span class="pill pill-cat${p.id === pick.cat ? ' pill-here' : ''}" data-goto="${p.id}"
          title="Перейти в «${esc(p.name)}»">${esc(p.name)}</span>`).join('');

  box.innerHTML = crumbs + pick.tags.map(id => `
    <span class="pill">${esc(name(id))}<button class="x" data-drop="${id}" title="Убрать тег">
      <svg class="ic"><use href="#i-x"></use></svg></button></span>`).join('')
    + pick.exts.map(e => `
    <span class="pill">${esc(e)}<button class="x" data-dropext="${esc(e)}" title="Убрать формат">
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
      ? `<div class="group"><h3>Каталог</h3>${renderTree(catInfo.tree, pick.cat)}</div>`
      : '';
    $('panel').querySelectorAll('[data-goto]').forEach(el => {
      el.onclick = () => enterCatalog(+el.dataset.goto);
    });
    return;
  }

  const savedBlock = saved.length ? `
    <div class="saved">
      <h3>Сохранённые отборы</h3>
      ${saved.map(s => `
        <div class="saved-row">
          <button class="nm" data-apply="${s.id}" title="${esc(s.name)}">${esc(s.name)}</button>
          <button class="del" data-del="${s.id}" title="Удалить отбор">
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
      g.isMulti ? '' : ' <span class="single">один тег</span>')).join('');

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
  const DATES = { any: 'Любая дата', created: 'Создан', modified: 'Изменён' };
  const dateBlock = grp('Дата', `
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
        <input type="date" id="from" value="${pick.from}" title="с этой даты">
        <span class="dash">до</span>
        <input type="date" id="to" value="${pick.to}" title="по эту дату">
      </div>`);

  $('panel').innerHTML = savedBlock + showBlock + formatBlock + dateBlock + groups;

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

function renderResults() {
  const box = $('results');

  if (!res.files.length) {
    // Пусто по-разному: библиотека и правда пуста или отбор ничего не поймал.
    const filtered = pick.tags.length || pick.exts.length || pick.text || pick.from || pick.to;
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-grid"></use></svg></div>
        <h2>${filtered ? 'Ничего не нашлось' : 'В библиотеке пусто'}</h2>
        <p>${filtered
          ? (pick.from || pick.to
            ? 'Раздвинь диапазон дат, сними лишний тег или переключи И на ИЛИ.'
            : 'Сними лишний тег или переключи И на ИЛИ, тогда хватит любого из выбранных.')
          : 'Файлы появятся здесь, когда разложишь то, что лежит в приёме.'}</p>
      </div>`;
    return;
  }

  box.innerHTML = `<div class="grid">` + res.files.map(f => `
    <div class="card${f.kind === 'catalog' ? ' card-cat' : ''}${f.movedTo ? ' card-away' : ''}"
         data-id="${f.id}" data-kind="${f.kind || 'file'}" draggable="true"
         title="${tip(f)}">
      <div class="thumb">
        ${f.kind === 'catalog'
          ? `<svg class="ic"><use href="#i-folder"></use></svg>`
          : ftypeIcon(f.ext)}
        ${f.kind === 'catalog' ? ''
          : `<img loading="lazy" src="/api/thumb/${f.id}" alt="" onload="this.classList.add('ok')" onerror="this.remove()">`}
        ${f.movedTo ? `<span class="away" title="Уехал из хранилища: ${esc(f.movedTo)}">уехал</span>` : ''}
      </div>
      <div class="name">${esc(f.name)}</div>
    </div>`).join('') + `</div>`
    + (res.files.length < res.total
      ? `<div class="more"><button class="btn" id="more">Показать ещё ${num(res.total - res.files.length)}</button></div>`
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
  const tags = (f.tags || []).map(id => name(id)).filter(Boolean);
  const parts = [f.name];
  if (tags.length) parts.push(tags.join(', '));
  if (f.movedTo) parts.push('Уехал из хранилища: ' + f.movedTo);
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
  catch (e) { toast('Не открылось: ' + e.message, 'err'); }
}

/// Открыть файл тем, чем его открывает система.
async function openFile(id) {
  try { await jsend('/api/files/open', 'POST', { id }); }
  catch (e) { toast('Не открылось: ' + e.message, 'err'); }
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
    toast('Не удалилось: ' + e.message, 'err');
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
    : `<div class="pop-none">Такого тега нет</div>`;

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

document.addEventListener('keydown', e => {
  if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); $('q').focus(); $('q').select(); }
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
const num = n => (n || 0).toLocaleString('ru-RU');

function fmtSize(b) {
  if (!b) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return (i === 0 ? b : b.toFixed(b < 10 ? 1 : 0)) + ' ' + u[i];
}

function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
}

loadSaved();
load();
// Библиотека пополняется на экране разбора, обновляемся при возврате в окно.
addEventListener('focus', () => load());
