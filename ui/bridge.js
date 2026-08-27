'use strict';

// Мост экрана к программе, изнутри кадра.
//
// Напрямую его нет: сообщение из подкадра уходит в другое событие, на
// которое программа не подписана, а сообщения от программы приходят только
// в верхний документ. Поэтому всё идёт через оболочку, см. shell.js.
//
// Признаком «мы внутри программы» служит именно функция оболочки, а НЕ
// window.chrome.webview: сам объект WebView2 внедряет и в подкадры тоже,
// поэтому проверка на него в кадре всегда истинна. Код с такой проверкой
// выглядел бы живым и молча ничего не делал, а это худший вид поломки.

var tanbaHost = null;
try { if (typeof parent.__tanbaPost === 'function') tanbaHost = parent.__tanbaPost; } catch (e) { }

/// <summary>Отправить сообщение программе. Вне программы просто ничего.</summary>
function tanbaPost(msg) { if (tanbaHost) tanbaHost(msg); }

/// Слушатели сообщений от программы, по виду сообщения.
const tanbaHandlers = {};
function tanbaOn(kind, fn) { (tanbaHandlers[kind] ||= []).push(fn); }

addEventListener('message', e => {
  if (e.origin !== location.origin) return;
  const m = e.data && e.data.__tanba;
  if (!m || !m.kind) return;
  for (const fn of tanbaHandlers[m.kind] || []) fn(m);
});

// ── Размер плиток ───────────────────────────────────────────────────────
// Живёт в кадре, потому что и переключатель, и сетка тоже в кадре. А выбор
// общий на все экраны, и раньше он переносился сам собой: каждый переход
// был новым документом и перечитывал localStorage при рождении. Живые кадры
// так не делают, поэтому слушаем событие хранилища: браузер шлёт его всем
// документам того же происхождения, кроме того, который писал.

(function () {
  const files = document.querySelector('.files');
  const seg = document.getElementById('tile');
  if (!files) return;

  function paint(size) {
    files.classList.remove('tile-s', 'tile-l');
    if (size === 's' || size === 'l') files.classList.add('tile-' + size);
    if (seg) for (const b of seg.children) b.classList.toggle('on', b.dataset.tile === size);
  }

  if (seg) for (const b of seg.children) {
    b.onclick = () => {
      paint(b.dataset.tile);
      localStorage.setItem('tanba-tile', b.dataset.tile);
    };
  }

  paint(localStorage.getItem('tanba-tile') || 'm');
  addEventListener('storage', e => {
    if (e.key === 'tanba-tile') paint(e.newValue || 'm');
  });
})();

// Тема переключается в настройках, то есть в соседнем кадре. Оболочка
// узнаёт об этом сама, а каждому экрану надо перекраситься у себя.
addEventListener('storage', e => {
  if (e.key !== 'tanba-theme') return;
  document.documentElement.dataset.tanba = e.newValue === 'light' ? 'light' : 'dark';
});
