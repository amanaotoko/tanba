'use strict';

// Выделение, контекстное меню и модалки библиотеки.
// Живёт отдельно от library.js, но в той же области видимости: обработчики
// вешаются делегированием на контейнер, поэтому перерисовка сетки их не рвёт.

let sel = new Set();
let anchor = null;

const ids = () => [...sel];
const byId = id => res.files.find(f => f.id === id);

// ── Выделение ──────────────────────────────────────────────────────────

function applySel() {
  document.querySelectorAll('#results .card').forEach(el => {
    el.classList.toggle('sel', sel.has(+el.dataset.id));
  });
  updateStatus();
  window.tanbaCmdSync?.();
}

// Что командная панель и выделение должны знать про этот экран,
// см. cmdbar.js и picking.js.
//
// Каталог здесь настоящий, поэтому меню у него своё: открывать его значит
// войти внутрь, а удалять значит порвать связь, файлы при этом остаются.
window.tanbaCmd = {
  selected: () => [...sel],
  byId,
  reload: () => load(),
  tagName: id => name(id),
  enter: id => enterCatalog(id),
  delCatalog: file => deleteCatalogModal(file),

  box: () => $('results'),
  setSel: list => { sel.clear(); for (const id of list) sel.add(id); applySel(); },

  menu: file => file?.kind === 'catalog'
    ? ['enter', 'rename', 'tags', '-', 'delcat']
    : ['open', 'reveal', 'rename', 'tags', 'tocat', '-', 'del'],

  extra: (act, file) => {
    if (act === 'enter') return enterCatalog(file.id);
    if (act === 'reveal') return reveal(file.id);
    if (act === 'tocat') return toast('Выбор каталога делаю следующим');
    if (act === 'delcat') return deleteCatalogModal(file);
  },
};

// Строка состояния как в проводнике: сколько всего, сколько выделено
// и сколько это весит. Слова склоняются, «2 элементов» не бывает.
function updateStatus() {
  const total = res.total || 0;
  $('stCount').textContent = `Элементов: ${num(total)}`;

  const picked = res.files.filter(f => sel.has(f.id));
  if (picked.length) {
    const bytes = picked.reduce((s, f) => s + (f.size || 0), 0);
    const word = plural(picked.length, 'элемент', 'элемента', 'элементов');
    // Глагол согласуется с существительным: «Выбран 1 элемент», но
    // «Выбрано 2 элемента». Форму берём из самого слова, чтобы они
    // не разъехались, если правило склонения когда-нибудь поправят.
    const verb = word === 'элемент' ? 'Выбран' : 'Выбрано';
    $('stSel').textContent =
      `${verb} ${num(picked.length)} ${word}` + (bytes ? `, ${fmtSize(bytes)}` : '');
  } else {
    $('stSel').textContent = '';
  }

  $('stBytes').textContent = res.bytes ? fmtSize(res.bytes) : '';
}

// Сетка перерисовывается целиком, поэтому подсветку выделения возвращаем
// после каждой отрисовки, а не вешаем на элементы.
const _renderResults = renderResults;
renderResults = function () { _renderResults(); applySel(); };

function pickCard(id, e) {
  const order = res.files.map(f => f.id);
  if (e.shiftKey && anchor !== null) {
    const a = order.indexOf(anchor), b = order.indexOf(id);
    if (a >= 0 && b >= 0) {
      if (!e.ctrlKey) sel.clear();
      for (let i = Math.min(a, b); i <= Math.max(a, b); i++) sel.add(order[i]);
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
  applySel();
}

$('results').addEventListener('click', e => {
  const card = e.target.closest('.card');
  // Клик по чипу тега добавляет его в отбор, выделение тут ни при чём.
  if (!card || e.target.closest('[data-add]')) return;
  pickCard(+card.dataset.id, e);
});

// ── Модалки ────────────────────────────────────────────────────────────

let modal = null;

function closeModal() { modal?.remove(); modal = null; }
addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

function openModal(html) {
  closeModal();
  modal = document.createElement('div');
  modal.className = 'modal';
  modal.innerHTML = `<div class="modal-box">${html}</div>`;
  // Клик по затемнению закрывает, клик внутри окна нет.
  modal.onclick = e => { if (e.target === modal) closeModal(); };
  document.body.appendChild(modal);
  return modal.querySelector('.modal-box');
}

function deleteCatalogModal(file) {
  const box = openModal(`
    <h2>Удалить каталог «${esc(file.name)}»?</h2>
    <p>Файлы остаются на диске и в библиотеке, их теги сохраняются.
       Пропадёт только связь: ${file.count || 0}
       ${plural(file.count || 0, 'объект перестанет', 'объекта перестанут', 'объектов перестанут')}
       лежать в этом каталоге.</p>
    <div class="modal-acts">
      <button class="btn" data-no>Отмена</button>
      <button class="btn btn-danger" data-yes>Удалить связь</button>
    </div>`);

  box.querySelector('[data-yes]').onclick = async () => {
    await jsend('/api/catalogs/' + file.id, 'DELETE');
    closeModal();
    sel.clear();
    await load();
    toast('Каталог удалён, файлы на месте');
  };
  box.querySelector('[data-no]').onclick = closeModal;
}
