'use strict';

// Перетаскивание файлов из окна наружу: в браузер, в папку, в другую программу.
// Страница отдать файл не может, ей доступен только текст, поэтому CF_HDROP
// делает C#. Здесь ровно два дела: сказать, что потащили, и погасить своё
// перетаскивание, чтобы два не шли одновременно.

(function () {
  var bridge = window.chrome && window.chrome.webview;
  if (!bridge) return;   // в обычном браузере нативного хоста нет, не мешаем

  // Тащат выделенное; если карточка не в выделении, берём только её.
  function idsFor(card) {
    var id = +card.dataset.id;
    var picked = [].map.call(document.querySelectorAll('.card.sel'), function (el) {
      return +el.dataset.id;
    });
    return picked.indexOf(id) >= 0 ? picked : [id];
  }

  // Делегирование на документе: карточки перерисовываются целиком каждые
  // несколько секунд, обработчики на самих элементах не пережили бы это.
  document.addEventListener('dragstart', function (e) {
    var card = e.target.closest && e.target.closest('.card[data-id]');
    if (!card) return;

    e.preventDefault();   // дальше ведёт C#
    var ids = idsFor(card);
    if (ids.length) bridge.postMessage({ kind: 'dragOut', ids: ids });
  }, true);

  // Файлы извне до страницы не доходят (AllowExternalDrop=false), так что
  // зону приёма зажигает C#.
  bridge.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || m.kind !== 'dropHint') return;
    var el = document.getElementById('drop');
    if (el) el.classList.toggle('on', !!m.on);
  });
})();
