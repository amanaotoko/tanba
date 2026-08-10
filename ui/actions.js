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

// Что командная панель должна знать про этот экран, см. cmdbar.js.
// Каталог здесь настоящий, поэтому отдаём и вход внутрь, и своё удаление:
// у каталога нет байтов, открывать и стирать его надо иначе, чем файл.
window.tanbaCmd = {
  selected: () => [...sel],
  byId,
  reload: () => load(),
  tagName: id => name(id),
  enter: id => enterCatalog(id),
  delCatalog: file => deleteCatalogModal(file),
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

// ── Рамка выделения ────────────────────────────────────────────────────
// Зажал на пустом месте и обвёл. Держим точки в координатах содержимого,
// а не окна: иначе при автопрокрутке рамка уползает от того, что обводишь.

(function marquee() {
  const box = $('results');
  let band = null, base = null, from = null, scroller = 0;

  const point = e => ({
    x: e.clientX - box.getBoundingClientRect().left + box.scrollLeft,
    y: e.clientY - box.getBoundingClientRect().top + box.scrollTop,
  });

  function draw(to) {
    const r = box.getBoundingClientRect();
    const x1 = Math.min(from.x, to.x), x2 = Math.max(from.x, to.x);
    const y1 = Math.min(from.y, to.y), y2 = Math.max(from.y, to.y);

    band.style.left = (r.left + x1 - box.scrollLeft) + 'px';
    band.style.top = (r.top + y1 - box.scrollTop) + 'px';
    band.style.width = (x2 - x1) + 'px';
    band.style.height = (y2 - y1) + 'px';

    // Пересечение считаем в координатах окна: карточки и рамка там оба.
    const bb = band.getBoundingClientRect();
    sel.clear();
    base.forEach(id => sel.add(id));
    document.querySelectorAll('#results .card').forEach(el => {
      const c = el.getBoundingClientRect();
      const hit = c.left < bb.right && c.right > bb.left && c.top < bb.bottom && c.bottom > bb.top;
      if (hit) sel.add(+el.dataset.id);
    });
    applySel();
  }

  box.addEventListener('mousedown', e => {
    // Только левой, только по пустому месту: на карточке живёт перетаскивание.
    if (e.button !== 0 || e.target.closest('.card')) return;
    e.preventDefault();

    from = point(e);
    base = (e.ctrlKey || e.metaKey || e.shiftKey) ? [...sel] : [];
    let started = false;
    let last = e;

    const move = ev => {
      last = ev;
      // Порог, чтобы обычный клик по пустому месту не считался рамкой.
      if (!started) {
        const p = point(ev);
        if (Math.abs(p.x - from.x) < 4 && Math.abs(p.y - from.y) < 4) return;
        started = true;
        band = document.createElement('div');
        band.className = 'marquee';
        document.body.appendChild(band);
      }
      draw(point(ev));
    };

    // Тянем за нижний край: список прокручивается сам, как в проводнике.
    scroller = setInterval(() => {
      if (!started) return;
      const r = box.getBoundingClientRect();
      const edge = 40;
      let dy = 0;
      if (last.clientY > r.bottom - edge) dy = last.clientY - (r.bottom - edge);
      else if (last.clientY < r.top + edge) dy = last.clientY - (r.top + edge);
      if (!dy) return;
      box.scrollTop += Math.sign(dy) * Math.min(24, Math.abs(dy) / 2);
      draw(point(last));
    }, 16);

    const up = () => {
      clearInterval(scroller);
      removeEventListener('mousemove', move);
      removeEventListener('mouseup', up);
      band?.remove();
      band = null;
      // Клик по пустому месту без движения снимает выделение.
      if (!started && !base.length) { sel.clear(); applySel(); }
    };

    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
})();

// ── Контекстное меню ───────────────────────────────────────────────────

let ctx = null;

function closeCtx() { ctx?.remove(); ctx = null; }

$('results').addEventListener('contextmenu', e => {
  const card = e.target.closest('.card');
  if (!card) return;
  e.preventDefault();

  const id = +card.dataset.id;
  // Правый клик по невыделенному берёт его одного, по выделенному
  // оставляет всю пачку: так же ведёт себя проводник.
  if (!sel.has(id)) { sel.clear(); sel.add(id); anchor = id; applySel(); }

  showCtx(e.clientX, e.clientY, byId(id));
});

function showCtx(x, y, file) {
  closeCtx();
  const many = sel.size > 1;
  const isCat = file && file.kind === 'catalog';

  const item = (icon, label, act, cls = '') =>
    `<button data-act="${act}" class="${cls}">
       <svg class="ic"><use href="#${icon}"></use></svg>${label}</button>`;

  const rows = isCat
    ? [item('i-folder', 'Открыть', 'enter'),
       item('i-pencil', 'Переименовать', 'rename'),
       item('i-tag', 'Настроить теги', 'tags'),
       '<div class="line"></div>',
       item('i-trash', 'Удалить каталог', 'delcat', 'danger')]
    : [item('i-play', many ? `Открыть (${sel.size})` : 'Открыть', 'open'),
       item('i-search', 'Показать в проводнике', 'reveal'),
       item('i-pencil', 'Переименовать', 'rename'),
       item('i-tag', many ? `Настроить теги (${sel.size})` : 'Настроить теги', 'tags'),
       item('i-folder', 'В каталог', 'tocat'),
       '<div class="line"></div>',
       item('i-trash', many ? `Удалить (${sel.size})` : 'Удалить', 'del', 'danger')];

  ctx = document.createElement('div');
  ctx.className = 'ctx';
  ctx.innerHTML = rows.join('');
  document.body.appendChild(ctx);

  // Меню не должно вылезать за окно: у нижнего края разворачиваем вверх.
  const w = ctx.offsetWidth, h = ctx.offsetHeight;
  ctx.style.left = Math.min(x, innerWidth - w - 8) + 'px';
  ctx.style.top = (y + h > innerHeight - 8 ? Math.max(8, y - h) : y) + 'px';

  ctx.querySelectorAll('[data-act]').forEach(b => {
    b.onclick = ev => { ev.stopPropagation(); closeCtx(); runAct(b.dataset.act, file); };
  });
}

addEventListener('click', closeCtx);
addEventListener('keydown', e => { if (e.key === 'Escape') { closeCtx(); closeModal(); } });
addEventListener('resize', closeCtx);

// ── Действия ───────────────────────────────────────────────────────────

async function runAct(act, file) {
  try {
    // Открыть, переименовать, теги и удалить живут в cmdbar.js: те же четыре
    // действия вынесены на командную панель, и держать их в двух местах
    // значит однажды получить кнопку и пункт меню с разным поведением.
    if (act === 'open' || act === 'rename' || act === 'tags' || act === 'del')
      return cmdAct(act);

    if (act === 'enter') return enterCatalog(file.id);
    if (act === 'reveal') return reveal(file.id);
    if (act === 'tocat') return toast('Выбор каталога делаю следующим');
    if (act === 'delcat') return deleteCatalogModal(file);
  } catch (e) {
    toast('Не вышло: ' + e.message, 'err');
  }
}

// ── Модалки ────────────────────────────────────────────────────────────

let modal = null;

function closeModal() { modal?.remove(); modal = null; }

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
