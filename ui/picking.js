'use strict';

// Рамка выделения и правое меню. Общее для разбора и библиотеки: экраны
// показывают одни и те же карточки, и вести себя при выделении обязаны
// одинаково. Раньше всё это жило в actions.js, а он подключён только
// к библиотеке, и на разборе не было ни рамки, ни меню.
//
// Экран рассказывает о себе через window.tanbaCmd, см. cmdbar.js. Здесь
// нужны ещё четыре вещи:
//
//   box()        где лежит сетка карточек
//   order()      номера карточек в том порядке, в каком они нарисованы
//   setSel(ids)  заменить выделение и перерисовать его
//   settled()    выделение устоялось, можно перечитать экран (необязательно)
//   menu(file)   свой набор пунктов меню (необязательно)
//   extra(a, f)  действия, которых нет в командной панели (необязательно)

const scr = () => window.tanbaCmd || {};
const scrBox = () => scr().box?.();
const scrSel = () => (scr().selected && scr().selected()) || [];

// ── Рамка выделения ────────────────────────────────────────────────────
// Зажал на пустом месте и обвёл. Точки держим в координатах содержимого,
// а не окна: иначе при автопрокрутке рамка уползает от того, что обводишь.

(function marquee() {
  const box = scrBox();
  if (!box) return;

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
    const hit = [...base];
    box.querySelectorAll('.card').forEach(el => {
      const c = el.getBoundingClientRect();
      if (c.left < bb.right && c.right > bb.left && c.top < bb.bottom && c.bottom > bb.top)
        hit.push(+el.dataset.id);
    });
    scr().setSel?.(hit);
  }

  box.addEventListener('mousedown', e => {
    // Только левой, только по пустому месту: на карточке живёт перетаскивание.
    if (e.button !== 0 || e.target.closest('.card')) return;
    e.preventDefault();

    from = point(e);
    base = (e.ctrlKey || e.metaKey || e.shiftKey) ? scrSel() : [];
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
      if (!started && !base.length) scr().setSel?.([]);
      // Экран мог считать состояние тегов под выделение: во время протяжки
      // дёргать сервер нельзя, а один раз в конце нужно.
      scr().settled?.();
    };

    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
})();

// ── Правое меню ────────────────────────────────────────────────────────

// Значок, подпись и надо ли считать выделенные в подписи.
const ACTS = {
  open:   ['i-play', 'Открыть', true],
  reveal: ['i-search', 'Показать в проводнике', false],
  rename: ['i-pencil', 'Переименовать', false],
  tags:   ['i-tag', 'Настроить теги', true],
  tocat:  ['i-folder', 'В каталог', false],
  del:    ['i-trash', 'Удалить', true, 'danger'],
  enter:  ['i-folder', 'Открыть', false],
  delcat: ['i-trash', 'Удалить каталог', false, 'danger'],
};

/// Набор по умолчанию. Библиотека подменяет его своим, потому что у неё
/// бывают каталоги и есть куда складывать.
const ACTS_DEFAULT = ['open', 'reveal', 'rename', 'tags', '-', 'del'];

/// Эти четыре умеет командная панель, остальное отдаём экрану.
const ACTS_SHARED = new Set(['open', 'rename', 'tags', 'del']);

let ctx = null;

function closeCtx() { ctx?.remove(); ctx = null; }

function showCtx(x, y, file) {
  closeCtx();
  const n = scrSel().length;
  const rows = (scr().menu?.(file) || ACTS_DEFAULT).map(key => {
    if (key === '-') return '<div class="line"></div>';
    const [icon, label, counted, cls = ''] = ACTS[key];
    return `<button data-act="${key}" class="${cls}">
       <svg class="ic"><use href="#${icon}"></use></svg>${
         counted && n > 1 ? `${label} (${n})` : label}</button>`;
  });

  ctx = document.createElement('div');
  ctx.className = 'ctx';
  ctx.innerHTML = rows.join('');
  document.body.appendChild(ctx);

  // Меню не должно вылезать за окно: у нижнего края разворачиваем вверх.
  const w = ctx.offsetWidth, h = ctx.offsetHeight;
  ctx.style.left = Math.min(x, innerWidth - w - 8) + 'px';
  ctx.style.top = (y + h > innerHeight - 8 ? Math.max(8, y - h) : y) + 'px';

  ctx.querySelectorAll('[data-act]').forEach(b => {
    b.onclick = ev => {
      ev.stopPropagation();
      closeCtx();
      const act = b.dataset.act;
      ACTS_SHARED.has(act) ? cmdAct(act) : scr().extra?.(act, file);
    };
  });
}

if (scrBox()) {
  scrBox().addEventListener('contextmenu', e => {
    const card = e.target.closest('.card');
    if (!card) return;
    e.preventDefault();

    const id = +card.dataset.id;
    // Правый клик по невыделенному берёт его одного, по выделенному
    // оставляет всю пачку: так же ведёт себя проводник.
    if (!scrSel().includes(id)) {
      scr().setSel?.([id]);
      scr().settled?.();
    }

    showCtx(e.clientX, e.clientY, scr().byId?.(id));
  });
}

addEventListener('click', closeCtx);
addEventListener('keydown', e => { if (e.key === 'Escape') closeCtx(); });
addEventListener('resize', closeCtx);
