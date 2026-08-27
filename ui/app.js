'use strict';

const API = '';
let state = { inbox: [], groups: [], stats: {} };
let sel = new Set();          // выделенные файлы, якорь для Shift в picking.js

// Отбор приёма. Имена не pick и не toggleTag: на этом экране так уже
// называются выбор файлов и навешивание тега, и путать их нельзя.
let query = { tags: [], mode: 'and', text: '' };

const $ = id => document.getElementById(id);
const api = async (path, body) => {
  const r = await fetch(API + path, body === undefined ? {} : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
};

// ── Загрузка и отрисовка ───────────────────────────────────────────────

async function load() {
  const ids = [...sel].join(',');
  state = await api('/api/state' + (ids ? '?sel=' + ids : ''));
  // Файл мог уехать из приёма, чистим выделение от призраков.
  const alive = new Set(state.inbox.map(f => f.id));
  for (const id of [...sel]) if (!alive.has(id)) sel.delete(id);
  render();
}

function render() {
  renderFiles();
  renderSide();
  renderBar();
  renderScope();

  $('pending').textContent = state.pending ? tn(state.pending, 'inbox.pending') : '';
  // Куски разделены расстоянием и насыщенностью, а не точкой между ними.
  const s = state.stats || {};
  $('statFiles').textContent = t('inbox.stats.files', { n: I18N.num(s.files || 0) });
  $('statSize').textContent = fmtSize(s.bytes || 0);

  window.tanbaCmdSync?.();
}

/// Быстрая перерисовка одного лишь выделения. Рамка меняет его десятки раз
/// в секунду, и ходить за состоянием на сервер на каждое движение нельзя.
function paintSel() {
  document.querySelectorAll('#files .card').forEach(el => {
    el.classList.toggle('sel', sel.has(+el.dataset.id));
  });
  // Кнопка внизу теперь показывает размер выделения, а меняется он здесь,
  // десятки раз в секунду при рамке. Отрисовка полосы это несколько строк
  // текста, за сервером она не ходит.
  renderBar();
  window.tanbaCmdSync?.();
}

// Что командная панель и выделение должны знать про этот экран,
// см. cmdbar.js и picking.js. Каталогов в приёме не бывает, поэтому меню
// здесь обычное, без входа внутрь и без «в каталог»: класть в каталог
// можно только то, что уже разложено.
window.tanbaCmd = {
  selected: () => [...sel],
  byId: id => state.inbox.find(f => f.id === id),
  reload: () => load(),
  tagName: id => {
    for (const g of state.groups) for (const t of g.tags) if (t.id === id) return t.name;
    return '';
  },

  box: () => $('files'),
  // Порядок берём из того, что нарисовано, а не из всего приёма. Иначе
  // shift-клик при включённом отборе тянет за собой файлы, которых на
  // экране нет, и они уезжают в «Разложить» вместе с видимыми.
  order: () => visible().map(f => f.id),
  setSel: list => { sel.clear(); for (const id of list) sel.add(id); paintSel(); },
  // Флажки тегов слева считает сервер под выделение, поэтому после того,
  // как оно устоялось, экран надо перечитать.
  settled: () => load(),

  extra: (act, file) => {
    if (act === 'reveal') return api('/api/library/reveal', { id: file.id });
  },
};

/// Что сейчас нарисовано в сетке, чтобы понимать, надо ли её пересобирать.
let shownIds = [];

/// Подсказка карточки: имя и разметка. Теги переехали сюда с самой карточки,
/// где они переносились и делали ряды разной высоты. На этом экране они видны
/// ещё и в панели: выдели файл, и флажки покажут, чем он помечен.
function tip(f, tagName) {
  const tags = (f.tags || []).map(id => tagName[id]).filter(Boolean);
  return tags.length ? f.name + '\n' + tags.join(', ') : f.name;
}

/// То же самое для вставки в разметку. Перенос строки внутри значения
/// атрибута приходится писать мнемоникой, зато при живом узле её писать
/// нельзя: мнемоники расшифровывает разбор разметки, а не установка свойства.
const tipAttr = (f, tagName) => esc(tip(f, tagName)).replace(/\n/g, '&#10;');

/// Правит уже нарисованную карточку. Эскиз не трогаем намеренно.
function patchCard(box, f, tagName) {
  const el = box.querySelector(`.card[data-id="${f.id}"]`);
  if (!el) return;
  el.classList.toggle('sel', sel.has(f.id));
  const t = tip(f, tagName);
  if (el.title !== t) el.title = t;
}

function renderFiles() {
  const box = $('files');
  if (!state.inbox.length) {
    shownIds = [];
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-inbox"></use></svg></div>
        <h2>${esc(t('inbox.empty.title'))}</h2>
        <p>${esc(t('inbox.empty.body'))}</p>
      </div>`;
    return;
  }

  // Пусто по отбору это не то же самое, что пусто вообще: в первом случае
  // человеку надо сказать, что файлы есть, просто их сейчас не видно.
  const list = visible();
  if (!list.length) {
    shownIds = [];
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-search"></use></svg></div>
        <h2>${esc(t('inbox.noMatch.title'))}</h2>
        <p>${esc(tn(state.inbox.length, 'inbox.noMatch.body'))}</p>
      </div>`;
    return;
  }

  const tagName = {};
  for (const g of state.groups) for (const t of g.tags) tagName[t.id] = t.name;

  // Тот же набор файлов в том же порядке: сетку не пересобираем, а правим
  // то, что могло измениться. Пересборка через innerHTML создаёт теги img
  // заново, картинки уходят грузиться с нуля и все эскизы моргают.
  // А сюда мы попадаем на каждый клик по файлу и на каждый поставленный тег,
  // потому что состояние флажков считает сервер и его надо перезапросить.
  const ids = list.map(f => f.id);
  if (ids.length === shownIds.length && ids.every((id, i) => id === shownIds[i])
      && box.querySelector('.grid')) {
    for (const f of list) patchCard(box, f, tagName);
    return;
  }
  shownIds = ids;

  box.innerHTML = `<div class="grid">` + list.map(f => `
    <div class="card${sel.has(f.id) ? ' sel' : ''}" data-id="${f.id}" draggable="true"
         title="${tipAttr(f, tagName)}">
      <div class="thumb">
        ${ftypeIcon(f.ext)}
        ${ftypeBadge(f.ext)}
        <img loading="lazy" src="/api/thumb/${f.id}${f.modifiedAt ? '?v=' + f.modifiedAt : ''}" alt="" onload="this.classList.add('ok')" onerror="this.remove()">
      </div>
      <div class="name">${esc(f.name)}</div>
    </div>`).join('') + `</div>`;

  // Выделение по клику вешает picking.js, одним слушателем на всю сетку
  // и одинаково с библиотекой. Здесь остаётся двойной клик: им открывается
  // сам файл, потому что перед тем как вешать теги обычно надо посмотреть,
  // что это вообще такое, а по эскизу видно не всё.
  box.querySelectorAll('.card').forEach(el => {
    el.ondblclick = async () => {
      try { await api('/api/files/open', { id: +el.dataset.id }); }
      catch (e) { toast(t('toast.openFailed', { error: e.message }), 'err'); }
    };
  });
}

// Свёрнутые группы переживают перезапуск и общие с библиотекой:
// это одна и та же панель, незачем сворачивать «Организацию» дважды.
const collapsed = new Set(JSON.parse(localStorage.getItem('tanba-collapsed') || '[]'));

function toggleGroup(name) {
  collapsed.has(name) ? collapsed.delete(name) : collapsed.add(name);
  localStorage.setItem('tanba-collapsed', JSON.stringify([...collapsed]));
  renderSide();
}

/// Слепок того, от чего зависит панель. Пока он тот же, панель не трогаем:
/// перерисовка сбрасывает фокус, и набранное в поле «новый тег» пропадало бы
/// при каждом обновлении состояния.
let sideKey = '';

function renderSide() {
  const key = JSON.stringify([sel.size, [...collapsed],
    state.groups.map(g => [g.id, g.name, g.isMulti,
      g.tags.map(t => [t.id, t.name, t.state, t.count])])]);
  if (key === sideKey) return;
  sideKey = key;

  const n = sel.size;
  // Ни одного тега: звать выделять файлы бессмысленно, вешать на них нечего.
  // Панель молчала бы пустым местом, поэтому вместо приглашения выделять
  // говорим, где теги заводятся, и уводим туда же.
  const top = !state.groups.some(g => g.tags.length)
    ? `<div class="dim">${esc(t('tags.none.title'))}</div>` +
      `<a class="btn btn-ghost" href="tags.html">${esc(t('tags.none.note'))}</a>`
    : n ? `<div class="dim">${esc(tn(n, 'inbox.side.selected'))}</div>`
    : `<div class="dim">${esc(t('inbox.side.hint'))}</div>`;

  $('side').innerHTML = top + state.groups.map(g => `
    <div class="group${collapsed.has(g.name) ? ' closed' : ''}" data-group="${g.id}">
      <h3 data-collapse="${esc(g.name)}">${esc(g.name)}${g.isMulti ? '' : ` <span class="single">${esc(t('tag.group.single'))}</span>`}<svg class="ic chev"><use href="#i-chev"></use></svg></h3>
      ${g.tags.map(t => `
        <button class="tag" data-tag="${t.id}" data-state="${t.state}" ${n ? '' : 'disabled'}>
          <span class="box ${t.state}${g.isMulti ? '' : ' radio'}">
            <svg class="ic"><use href="#${t.state === 'partial' ? 'i-minus' : 'i-check'}"></use></svg>
          </span>
          <span class="lbl">${esc(t.name)}</span>
          <span class="n">${t.count ? I18N.num(t.count) : ''}</span>
        </button>`).join('')}
      <div class="newtag">
        <input placeholder="${esc(t('common.tag.new'))}" data-newtag="${g.id}">
      </div>
    </div>`).join('');

  $('side').querySelectorAll('[data-collapse]').forEach(el => {
    el.onclick = () => toggleGroup(el.dataset.collapse);
  });
  $('side').querySelectorAll('.tag').forEach(el => {
    el.onclick = () => toggleTag(+el.dataset.tag, el.dataset.state);
  });
  $('side').querySelectorAll('[data-newtag]').forEach(el => {
    el.onkeydown = async e => {
      if (e.key !== 'Enter' || !el.value.trim()) return;
      const { id } = await api('/api/tags', { groupId: +el.dataset.newtag, name: el.value.trim() });
      el.value = '';
      if (sel.size) await api('/api/tag', { fileIds: [...sel], tagId: id, on: true });
      await load();
    };
  });
}

function renderBar() {
  // Число на кнопке это то, что уедет по нажатию, то есть выделенное.
  // Раньше оно считало все размеченные файлы приёма, и человек читал его
  // как «сколько выбрано», а уезжало совсем другое.
  const bare = state.inbox.length - state.inbox.filter(f => f.tags.length).length;
  $('fileN').textContent = I18N.num(sel.size);
  $('fileBtn').disabled = !sel.size;
  $('hint').textContent = bare ? tn(bare, 'inbox.bar.untagged') : '';
}

// ── Действия ───────────────────────────────────────────────────────────

async function toggleTag(tagId, cur) {
  if (!sel.size) return;
  // Промежуточное состояние: первый клик ставит тег всем.
  await api('/api/tag', { fileIds: [...sel], tagId, on: cur !== 'on' });
  await load();
}

$('fileBtn').onclick = async () => {
  // Уезжает выделенное, и только оно. Раньше здесь стояли все размеченные
  // файлы приёма: человек выделял пять, а уезжало сорок, включая скрытые
  // отбором, которых он в тот момент вообще не видел.
  const ids = [...sel];
  if (!ids.length) return;

  // Файл без тегов в хранилище почти не найти: теги там единственный
  // указатель. Поэтому не отправляем ничего, пока в выделении есть
  // неразмеченные, и говорим, сколько их.
  const bare = ids.filter(id => !state.inbox.find(f => f.id === id)?.tags.length);
  if (bare.length) { toast(tn(bare.length, 'toast.needTags'), 'err'); return; }

  // Запрос долгий: сервер внутри него заново обходит весь приём и переписывает
  // выгрузку каталога. Пока он идёт, кнопку гасим, иначе второе нажатие уходит
  // с тем же списком, и половина файлов приезжает уже перенесённой.
  const btn = $('fileBtn');
  btn.disabled = true;
  try {
    const r = await api('/api/file', { fileIds: ids, allowUntagged: false });
    sel.clear();

    const parts = [t('toast.filed', { n: I18N.num(r.moved) })];
    if (r.merged) parts.push(t('toast.filed.merged', { n: I18N.num(r.merged) }));
    // Файл мог лишиться тегов между отрисовкой и нажатием. Сервер такие
    // пропускает, и молчать об этом нельзя: человек ждал, что уедут все.
    if (r.skipped) parts.push(t('toast.filed.skipped', { n: I18N.num(r.skipped) }));

    // Ошибку надо назвать, а не покрасить. Раньше список r.errors только менял
    // цвет тоста, и человек видел красное «Разложено: 39», не зная, что с сороковым.
    const bad = r.errors || [];
    toast(bad.length
            ? bad[0] + (bad.length > 1 ? ' ' + t('toast.andMore', { n: I18N.num(bad.length - 1) }) : '')
            : parts.join(', '),
          bad.length ? 'err' : '');
  } catch (e) {
    // Раньше упавший запрос не показывал ничего: ни сообщения, ни обновления.
    // Человек видел прежний экран и не знал, уехало что-нибудь или нет.
    toast(t('toast.fileFailed', { error: e.message }), 'err');
  } finally {
    btn.disabled = false;
    await load();
  }
};

$('rescan').onclick = async () => { await api('/api/rescan', {}); await load(); };

// ── Отбор приёма ───────────────────────────────────────────────────────
// Считается на месте, без сервера: и список приёма, и теги каждого файла
// уже приехали с /api/state, спрашивать их заново незачем.

function visible() {
  const q = query.text.trim().toLowerCase();
  return state.inbox.filter(f => {
    if (q && !f.name.toLowerCase().includes(q)) return false;
    if (!query.tags.length) return true;
    const has = id => f.tags.includes(id);
    return query.mode === 'and' ? query.tags.every(has) : query.tags.some(has);
  });
}

function nameOf(tagId) {
  for (const g of state.groups) for (const t of g.tags) if (t.id === tagId) return t.name;
  return '?';
}

function toggleQueryTag(id) {
  const i = query.tags.indexOf(id);
  if (i < 0) query.tags.push(id); else query.tags.splice(i, 1);
  render();
}

function renderScope() {
  const box = $('scope');
  // Значок раздела в начале, как в библиотеке и как в проводнике. Выходить
  // отсюда некуда, поэтому он просто метка места и не нажимается.
  box.innerHTML = `
    <span class="pill pill-cat pill-root" title="${esc(t('inbox.scope.root'))}">
      <svg class="ic"><use href="#i-inbox"></use></svg>
    </span>` + query.tags.map(id => `
    <span class="pill">${esc(nameOf(id))}<button class="x" data-drop="${id}" title="${esc(t('scope.removeTag'))}">
      <svg class="ic"><use href="#i-x"></use></svg></button></span>`).join('');
  box.querySelectorAll('[data-drop]').forEach(el => {
    el.onclick = () => toggleQueryTag(+el.dataset.drop);
  });

  for (const b of $('mode').children) b.classList.toggle('on', b.dataset.mode === query.mode);
  // Сброс гаснет, а не пропадает: исчезающая кнопка двигала бы всю строку.
  $('reset').disabled = !(query.tags.length || query.text);
}

$('q').oninput = () => { query.text = $('q').value; render(); };
for (const b of $('mode').children) {
  b.onclick = () => { query.mode = b.dataset.mode; render(); };
}
$('reset').onclick = () => {
  query.tags = [];
  query.text = '';
  $('q').value = '';
  render();
};

// ── Выбор тега для отбора ──────────────────────────────────────────────

let popRows = [], popCur = 0;

function openPop() {
  const r = $('add').getBoundingClientRect();
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
  for (const g of state.groups)
    for (const t of g.tags)
      if (!query.tags.includes(t.id) && (!s || t.name.toLowerCase().includes(s)))
        popRows.push({ t, g });
  popCur = 0;

  $('popList').innerHTML = popRows.length
    ? popRows.map((r, i) => `
      <button class="pop-row${i === popCur ? ' cur' : ''}" data-p="${r.t.id}">
        <span class="nm">${esc(r.t.name)}</span>
        <span class="gr">${esc(r.g.name)}</span>
      </button>`).join('')
    : `<div class="pop-none">${esc(t('common.tagNotFound'))}</div>`;

  $('popList').querySelectorAll('[data-p]').forEach(el => {
    el.onclick = () => { closePop(); toggleQueryTag(+el.dataset.p); };
  });
}

$('add').onclick = openPop;
$('popQ').oninput = fillPop;
$('popQ').onkeydown = e => {
  if (e.key === 'Escape') { closePop(); return; }
  if (e.key === 'Enter') {
    if (popRows[popCur]) { closePop(); toggleQueryTag(popRows[popCur].t.id); }
    return;
  }
  if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
  e.preventDefault();
  popCur = Math.max(0, Math.min(popRows.length - 1, popCur + (e.key === 'ArrowDown' ? 1 : -1)));
  const rows = $('popList').children;
  for (let i = 0; i < rows.length; i++) rows[i].classList.toggle('cur', i === popCur);
  if (rows[popCur]) rows[popCur].scrollIntoView({ block: 'nearest' });
};

// Клик мимо закрывает. Цель проверяем через closest: попасть можно
// и в значок внутри кнопки, а это уже другой элемент.
document.addEventListener('mousedown', e => {
  if (!$('pop').hidden && !$('pop').contains(e.target) && !e.target.closest('#add')) closePop();
});

// Escape и Ctrl+A переехали в picking.js: они про выделение, а выделение
// теперь одно на оба экрана. Здесь остаётся только своё.
document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || e.target.isContentEditable) return;

  // Поверх открытого окна Enter принадлежит окну, а не этой кнопке. Иначе
  // подтверждение удаления отвечало сразу двумя действиями: файлы уходили
  // в корзину И то же самое выделение уезжало в библиотеку. Тот же приём
  // стоит в picking.js, здесь его просто не было.
  if (document.querySelector('.modal')) return;
  if (document.querySelector('.ctx')) return;

  if (e.key === 'Enter' && !$('fileBtn').disabled) $('fileBtn').click();
});

// ── Приём файлов перетаскиванием ───────────────────────────────────────

let dragDepth = 0;
addEventListener('dragenter', e => { e.preventDefault(); if (++dragDepth === 1) $('drop').classList.add('on'); });
addEventListener('dragleave', e => { e.preventDefault(); if (--dragDepth <= 0) { dragDepth = 0; $('drop').classList.remove('on'); } });
addEventListener('dragover', e => e.preventDefault());
addEventListener('drop', e => {
  e.preventDefault();
  dragDepth = 0;
  $('drop').classList.remove('on');
  toast(t('toast.dropNotYet'));
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

const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

function fmtSize(b) {
  if (!b) return '';
  const u = [t('units.b'), t('units.kb'), t('units.mb'), t('units.gb'), t('units.tb')];
  let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return I18N.num(i === 0 ? b : b.toFixed(b < 10 ? 1 : 0)) + ' ' + u[i];
}

// Разметку заполняем до первого запроса: страница уже разобрана, и человек
// видит её на своём языке, не дожидаясь ответа сервера.
I18N.apply();

// Быстрая копия языка могла разойтись с настоящей настройкой: её выбирают
// в мастере, а у того своё окно и свой localStorage. Сверяемся и, если
// разошлись, перерисовываемся уже на верном языке.
I18N.sync(() => render());
load();

// Про пополнение приёма извне программа узнаёт от наблюдателя за папкой
// и говорит нам сама, сразу. Опрос по таймеру оставлен только страховкой
// на случай потерянного сообщения, и он редкий: при неизменных данных
// перерисовки всё равно не будет, но и лишний запрос раз в четыре секунды
// ни к чему.
const host = window.chrome && window.chrome.webview;
if (host) host.addEventListener('message', e => {
  if (e.data && e.data.kind === 'inbox') load();
});
setInterval(load, host ? 30000 : 4000);
