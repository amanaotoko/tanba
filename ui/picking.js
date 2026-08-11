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
const scrOrder = () => scr().order?.() || [];

/// Откуда shift отсчитывает диапазон. Живёт здесь, а не на экране: раньше
/// каждый экран вёл свой якорь, и вместе с ним свою копию правил выделения,
/// а разошлись в итоге Escape и Ctrl+A.
let anchor = null;

// ── Выделение по клику ─────────────────────────────────────────────────

function pickCard(id, e) {
  const order = scrOrder();
  const cur = scrSel();
  let next;

  if (e.shiftKey && anchor !== null && order.includes(anchor) && order.includes(id)) {
    const a = order.indexOf(anchor), b = order.indexOf(id);
    const range = order.slice(Math.min(a, b), Math.max(a, b) + 1);
    // Якорь при этом не двигаем: повторные shift-клики честно перерисовывают
    // диапазон от исходной точки, а не ползут за курсором.
    next = e.ctrlKey ? [...new Set([...cur, ...range])] : range;
  } else if (e.ctrlKey || e.metaKey) {
    next = cur.includes(id) ? cur.filter(x => x !== id) : [...cur, id];
    anchor = id;
  } else {
    // Повторный клик по уже выделенному ничего не снимает. Так ведёт себя
    // проводник, проверено на живом окне: три клика подряд по одному файлу
    // оставляют его выделенным. И так двойной клик перестаёт гасить выбор
    // своим вторым щелчком, а раньше гасил: файл открывался и переставал
    // быть выделенным. Снимает выделение только клик по пустому месту.
    next = [id];
    anchor = id;
  }

  scr().setSel?.(next);
  scr().settled?.();
}

if (scrBox()) {
  scrBox().addEventListener('click', e => {
    const card = e.target.closest('.card');
    // Клик по чипу тега добавляет его в отбор, выделение тут ни при чём.
    if (!card || e.target.closest('[data-add]')) return;
    pickCard(+card.dataset.id, e);
  });
}

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
      // Клик по пустому месту без движения снимает выделение. Якорь тоже:
      // иначе следующий shift-клик отсчитает диапазон от точки, которая
      // уже не выделена и о которой человек забыл.
      if (!started && !base.length) { scr().setSel?.([]); anchor = null; }
      // Экран мог считать состояние тегов под выделение: во время протяжки
      // дёргать сервер нельзя, а один раз в конце нужно.
      scr().settled?.();
    };

    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
})();

// ── Правое меню ────────────────────────────────────────────────────────

// Значок, ключ подписи и надо ли считать выделенные в подписи.
// Ключ, а не готовая строка: меню собирается заново на каждый показ,
// и подпись берётся на том языке, который выбран сейчас.
const ACTS = {
  open:   ['i-play', 'common.cmd.open', true],
  reveal: ['i-search', 'menu.reveal', false],
  rename: ['i-pencil', 'common.cmd.rename', false],
  tags:   ['i-tag', 'common.cmd.tags', true],
  tocat:  ['i-folder', 'menu.toCatalog', false],
  del:    ['i-trash', 'common.cmd.delete', true, 'danger'],
  enter:  ['i-folder', 'common.cmd.open', false],
  delcat: ['i-trash', 'menu.deleteCatalog', false, 'danger'],
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
    const [icon, str, counted, cls = ''] = ACTS[key];
    const label = t(str);
    return `<button data-act="${key}" class="${cls}">
       <svg class="ic"><use href="#${icon}"></use></svg>${
         counted && n > 1 ? `${label} (${I18N.num(n)})` : label}</button>`;
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
addEventListener('resize', closeCtx);

// ── Клавиши выделения ──────────────────────────────────────────────────

/// Человек печатает: клавиши принадлежат полю, а не сетке.
const typing = e => e.target.tagName === 'INPUT' || e.target.isContentEditable;

// Фаза перехвата, третьим аргументом. Окно переименования закрывает себя
// своим же обработчиком Escape, и в обычной фазе оно успело бы исчезнуть
// раньше, чем мы проверим, открыто ли оно: отмена переименования заодно
// сбрасывала бы выделение.
addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    // Escape снимает то, что сверху. Если открыто меню или окно, выделение
    // не трогаем.
    if (ctx) return closeCtx();
    if (document.querySelector('.modal')) return;
    if (typing(e)) return;
    scr().setSel?.([]);
    anchor = null;
    scr().settled?.();
    return;
  }

  // Проверяем код клавиши, а не букву. По букве не работало в русской
  // раскладке: оттуда приходит «ф», и сочетание молча ничего не делало.
  // CapsLock ломал его так же, потому что приходила заглавная «A».
  if (e.ctrlKey && e.code === 'KeyA' && !typing(e)) {
    e.preventDefault();
    // Берём то, что нарисовано на экране, а не весь раздел: под включённым
    // отбором «выделить всё» должно означать всё видимое, как в проводнике.
    scr().setSel?.(scrOrder());
    anchor = null;
    scr().settled?.();
  }
}, true);
