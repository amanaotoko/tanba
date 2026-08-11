'use strict';

// Выделение, контекстное меню и модалки библиотеки.
// Живёт отдельно от library.js, но в той же области видимости: обработчики
// вешаются делегированием на контейнер, поэтому перерисовка сетки их не рвёт.

let sel = new Set();          // якорь для Shift живёт в picking.js

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
  order: () => res.files.map(f => f.id),
  setSel: list => { sel.clear(); for (const id of list) sel.add(id); applySel(); },

  menu: file => file?.kind === 'catalog'
    ? ['enter', 'rename', 'tags', '-', 'delcat']
    : ['open', 'reveal', 'rename', 'tags', 'tocat', '-', 'del'],

  extra: (act, file) => {
    if (act === 'enter') return enterCatalog(file.id);
    if (act === 'reveal') return reveal(file.id);
    if (act === 'tocat') return toast(t('toast.catalogPickerSoon'));
    if (act === 'delcat') return deleteCatalogModal(file);
  },
};

// Строка состояния как в проводнике: сколько всего, сколько выделено
// и сколько это весит. Слова склоняются, «2 элементов» не бывает.
function updateStatus() {
  const total = res.total || 0;
  $('stCount').textContent = tn(total, 'lib.status.items');

  const picked = res.files.filter(f => sel.has(f.id));
  if (picked.length) {
    const bytes = picked.reduce((s, f) => s + (f.size || 0), 0);
    // Глагол согласуется с существительным: «Выбран 1 элемент», но
    // «Выбрано 2 элемента». В словаре каждая форма лежит целиком, вместе
    // с глаголом, поэтому разъехаться им негде.
    $('stSel').textContent =
      tn(picked.length, 'lib.status.selected') + (bytes ? `, ${fmtSize(bytes)}` : '');
  } else {
    $('stSel').textContent = '';
  }

  $('stBytes').textContent = res.bytes ? fmtSize(res.bytes) : '';
}

// Сетка перерисовывается целиком, поэтому подсветку выделения возвращаем
// после каждой отрисовки, а не вешаем на элементы.
const _renderResults = renderResults;
renderResults = function () { _renderResults(); applySel(); };

// Выделение по клику вешает picking.js, одинаково с разбором.

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
    <h2>${t('cat.del.title', { name: esc(file.name) })}</h2>
    <p>${t('cat.del.body', { items: tn(file.count || 0, 'cat.del.count') })}</p>
    <div class="modal-acts">
      <button class="btn" data-no>${t('common.cancel')}</button>
      <button class="btn btn-danger" data-yes>${t('cat.del.ok')}</button>
    </div>`);

  box.querySelector('[data-yes]').onclick = async () => {
    await jsend('/api/catalogs/' + file.id, 'DELETE');
    closeModal();
    sel.clear();
    await load();
    toast(t('toast.catalogDeleted'));
  };
  box.querySelector('[data-no]').onclick = closeModal;
}
