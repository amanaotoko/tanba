'use strict';

// Общие модалки. Системные confirm и prompt не знают про тёмную тему
// и выглядят чужими, поэтому свои.

function modalBox(html) {
  document.querySelector('.modal')?.remove();
  const wrap = document.createElement('div');
  wrap.className = 'modal';
  wrap.innerHTML = `<div class="modal-box">${html}</div>`;
  document.body.appendChild(wrap);
  return wrap;
}

/// Да или нет. Возвращает true, если согласились.
function askBox({ title, text, ok = t('common.cmd.delete'), danger = true }) {
  return new Promise(resolve => {
    const wrap = modalBox(`
      <h2>${title}</h2>
      ${text ? `<p>${text}</p>` : ''}
      <div class="modal-acts">
        <button class="btn" data-no>${t('common.cancel')}</button>
        <button class="btn ${danger ? 'btn-danger' : 'btn-primary'}" data-yes>${ok}</button>
      </div>`);

    const done = v => { wrap.remove(); document.removeEventListener('keydown', esc); resolve(v); };
    const esc = e => { if (e.key === 'Escape') done(false); };

    wrap.querySelector('[data-yes]').onclick = () => done(true);
    wrap.querySelector('[data-no]').onclick = () => done(false);
    wrap.onclick = e => { if (e.target === wrap) done(false); };
    document.addEventListener('keydown', esc);
    wrap.querySelector('[data-yes]').focus();
  });
}

/// Выбор одного из списка с поиском. Возвращает выбранный элемент или null.
function pickBox({ title, text, items, label = x => x.name, hint = () => '' }) {
  return new Promise(resolve => {
    const wrap = modalBox(`
      <h2>${title}</h2>
      ${text ? `<p>${text}</p>` : ''}
      <input class="name" id="pickq" placeholder="${t('modal.pick.search')}" spellcheck="false">
      <div class="pick-list" id="picklist"></div>
      <div class="modal-acts"><button class="btn" data-no>${t('common.cancel')}</button></div>`);

    const box = wrap.querySelector('#picklist');
    const q = wrap.querySelector('#pickq');

    const draw = () => {
      const needle = q.value.trim().toLowerCase();
      const found = items.filter(i => !needle || label(i).toLowerCase().includes(needle));
      box.innerHTML = found.length
        ? found.map((i, n) => `
            <button class="pick-row" data-n="${items.indexOf(i)}">
              <span class="nm">${label(i)}</span>
              <span class="gr">${hint(i)}</span>
            </button>`).join('')
        : `<div class="pop-none">${t('modal.pick.none')}</div>`;
      box.querySelectorAll('[data-n]').forEach(b => {
        b.onclick = () => done(items[+b.dataset.n]);
      });
    };

    const done = v => { wrap.remove(); document.removeEventListener('keydown', esc); resolve(v); };
    const esc = e => { if (e.key === 'Escape') done(null); };

    q.oninput = draw;
    wrap.querySelector('[data-no]').onclick = () => done(null);
    wrap.onclick = e => { if (e.target === wrap) done(null); };
    document.addEventListener('keydown', esc);
    draw();
    q.focus();
  });
}
