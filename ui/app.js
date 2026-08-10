'use strict';

const API = '';
let state = { inbox: [], groups: [], stats: {} };
let sel = new Set();          // выделенные файлы
let anchor = null;            // якорь для выделения с Shift

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

  $('pending').textContent = state.pending
    ? `${state.pending} ${plural(state.pending, 'файл', 'файла', 'файлов')} ждёт`
    : '';
  // Куски разделены расстоянием и насыщенностью, а не точкой между ними.
  const s = state.stats || {};
  $('statFiles').textContent = `${s.files || 0} в каталоге`;
  $('statSize').textContent = fmtSize(s.bytes || 0);
}

const ICON = ext => {
  if (/^(jpg|jpeg|png|gif|bmp|tif|tiff|webp|heic|svg)$/.test(ext || '')) return 'i-image';
  if (/^(mp4|mov|avi|mkv|webm|m4v)$/.test(ext || '')) return 'i-play';
  if (/^(pdf|docx?|xlsx?|pptx?|txt|rtf)$/.test(ext || '')) return 'i-doc';
  return 'i-file';
};

function renderFiles() {
  const box = $('files');
  if (!state.inbox.length) {
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-inbox"></use></svg></div>
        <h2>Разбирать нечего</h2>
        <p>Папка «СОХРАНИ СЮДА» пуста, всё разложено. Сохраняй туда что угодно, разберёшь потом одной пачкой.</p>
      </div>`;
    return;
  }

  // Пусто по отбору это не то же самое, что пусто вообще: в первом случае
  // человеку надо сказать, что файлы есть, просто их сейчас не видно.
  const list = visible();
  if (!list.length) {
    box.innerHTML = `
      <div class="empty">
        <div class="circle"><svg class="ic"><use href="#i-search"></use></svg></div>
        <h2>Под отбор ничего не подошло</h2>
        <p>В приёме ${state.inbox.length} ${plural(state.inbox.length, 'файл', 'файла', 'файлов')},
           но ни один не совпал. Сбрось отбор, чтобы увидеть всё.</p>
      </div>`;
    return;
  }

  const tagName = {};
  for (const g of state.groups) for (const t of g.tags) tagName[t.id] = t.name;

  box.innerHTML = `<div class="grid">` + list.map(f => `
    <div class="card${sel.has(f.id) ? ' sel' : ''}" data-id="${f.id}" draggable="true">
      <div class="thumb">
        <svg class="ic"><use href="#${ICON(f.ext)}"></use></svg>
        <img loading="lazy" src="/api/thumb/${f.id}" alt="" onload="this.classList.add('ok')" onerror="this.remove()">
      </div>
      <div class="name" title="${esc(f.name)}">${esc(f.name)}</div>
      <div class="meta"><span class="dim mono">${fmtSize(f.size)}</span></div>
      ${f.tags.length ? `<div class="chips">${f.tags.map(id => `<span class="chip">${esc(tagName[id] || '?')}</span>`).join('')}</div>` : ''}
    </div>`).join('') + `</div>`;

  box.querySelectorAll('.card').forEach(el => {
    el.onclick = e => pick(+el.dataset.id, e);
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

function renderSide() {
  const n = sel.size;
  const top = n
    ? `<div class="dim">Выделено: ${n} ${plural(n, 'файл', 'файла', 'файлов')}</div>`
    : `<div class="dim">Выдели файлы, чтобы вешать теги</div>`;

  $('side').innerHTML = top + state.groups.map(g => `
    <div class="group${collapsed.has(g.name) ? ' closed' : ''}" data-group="${g.id}">
      <h3 data-collapse="${esc(g.name)}">${esc(g.name)}${g.isMulti ? '' : ' <span class="single">один тег</span>'}<svg class="ic chev"><use href="#i-chev"></use></svg></h3>
      ${g.tags.map(t => `
        <button class="tag" data-tag="${t.id}" data-state="${t.state}" ${n ? '' : 'disabled'}>
          <span class="box ${t.state}${g.isMulti ? '' : ' radio'}">
            <svg class="ic"><use href="#${t.state === 'partial' ? 'i-minus' : 'i-check'}"></use></svg>
          </span>
          <span class="lbl">${esc(t.name)}</span>
          <span class="n">${t.count || ''}</span>
        </button>`).join('')}
      <div class="newtag">
        <input placeholder="новый тег…" data-newtag="${g.id}">
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
  // Разложить можно только размеченное, иначе файлы утекают в каталог молча.
  const ready = state.inbox.filter(f => f.tags.length).length;
  const bare = state.inbox.length - ready;
  $('fileN').textContent = ready;
  $('fileBtn').disabled = !ready;
  $('laterBtn').disabled = !bare;
  $('hint').textContent = bare ? `${bare} ${plural(bare, 'файл', 'файла', 'файлов')} без тегов` : '';
}

// ── Выделение ──────────────────────────────────────────────────────────

function pick(id, e) {
  const ids = state.inbox.map(f => f.id);
  if (e.shiftKey && anchor !== null) {
    const a = ids.indexOf(anchor), b = ids.indexOf(id);
    if (a >= 0 && b >= 0) {
      if (!e.ctrlKey) sel.clear();
      for (let i = Math.min(a, b); i <= Math.max(a, b); i++) sel.add(ids[i]);
    }
  } else if (e.ctrlKey || e.metaKey) {
    sel.has(id) ? sel.delete(id) : sel.add(id);
    anchor = id;
  } else {
    const only = sel.size === 1 && sel.has(id);
    sel.clear();
    if (!only) sel.add(id);
    anchor = id;
  }
  load();
}

// ── Действия ───────────────────────────────────────────────────────────

async function toggleTag(tagId, cur) {
  if (!sel.size) return;
  // Промежуточное состояние: первый клик ставит тег всем.
  await api('/api/tag', { fileIds: [...sel], tagId, on: cur !== 'on' });
  await load();
}

$('fileBtn').onclick = async () => {
  const ids = state.inbox.filter(f => f.tags.length).map(f => f.id);
  if (!ids.length) return;
  const r = await api('/api/file', { fileIds: ids, allowUntagged: false });
  sel.clear();
  const parts = [`Разложено: ${r.moved}`];
  if (r.merged) parts.push(`дубликатов слито: ${r.merged}`);
  toast(parts.join(', '), r.errors && r.errors.length ? 'err' : '');
  await load();
};

$('laterBtn').onclick = async () => {
  const ids = state.inbox.filter(f => !f.tags.length).map(f => f.id);
  if (!ids.length) return;
  const r = await api('/api/file', { fileIds: ids, allowUntagged: true });
  sel.clear();
  toast(`Отложено без тегов: ${r.moved}, ищи в «Неразобранном»`);
  await load();
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
    <span class="pill pill-cat pill-root" title="Приём">
      <svg class="ic"><use href="#i-inbox"></use></svg>
    </span>` + query.tags.map(id => `
    <span class="pill">${esc(nameOf(id))}<button class="x" data-drop="${id}" title="Убрать тег">
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
    : `<div class="pop-none">Такого тега нет</div>`;

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

document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT') return;
  if (e.ctrlKey && e.key === 'a') { e.preventDefault(); state.inbox.forEach(f => sel.add(f.id)); load(); }
  if (e.key === 'Escape') { sel.clear(); load(); }
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
  toast('Пока клади файлы в папку «СОХРАНИ СЮДА», приём перетаскиванием на подходе');
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

load();
setInterval(load, 4000);   // приём мог пополниться извне
