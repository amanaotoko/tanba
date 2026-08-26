'use strict';

// Полоса заголовка окна. Вкладки лежат прямо в ней, как в проводнике 11:
// заголовок мы забрали у системы себе, см. Host/MainWindow.Chrome.cs.
//
// Собирается кодом, а не переписывается в каждую страницу: экранов четыре,
// и четыре копии одной шапки разъехались бы на первой же правке.

(function () {
  // Тема из прошлого запуска. Живёт здесь, потому что этот файл есть на всех
  // экранах, а переключатель остался ровно один и лежит в Настройках.
  if (localStorage.getItem('tanba-theme') === 'light') {
    document.documentElement.dataset.tanba = 'light';
  }

  // Имя вкладки лежит рядом с ключом: русское остаётся в разметке запасным,
  // а заполнение по data-i18n перекрывает его выбранным языком. Так вкладки
  // следуют за языком и после переключения, а не только при первой сборке.
  var SCREENS = [
    { href: 'index.html', name: 'Разбор', key: 'nav.triage', icon: 't-inbox' },
    { href: 'library.html', name: 'Библиотека', key: 'nav.library', icon: 't-grid' },
    { href: 'tags.html', name: 'Теги', key: 'nav.tags', icon: 't-tag' },
    { href: 'settings.html', name: 'Настройки', key: 'nav.settings', icon: 't-cog' }
  ];

  var host = document.getElementById('chrome');
  if (!host) return;

  var here = location.pathname.split('/').pop() || 'index.html';

  var tabs = SCREENS.map(function (s) {
    return '<a class="tab' + (s.href === here ? ' on' : '') + '" href="' + s.href + '">' +
           '<svg class="ic"><use href="#' + s.icon + '"></use></svg>' +
           '<span data-i18n="' + s.key + '">' + s.name + '</span></a>';
  }).join('');

  // Клик по вкладке, которая и так открыта, раньше честно перезагружал
  // страницу: ссылка ведёт на самоё себя, и никто её не гасил. Окно гасло,
  // шапка собиралась заново, выделение и прокрутка терялись, и всё это ради
  // нулевого результата. Как в проводнике: ты уже здесь, нажатие пустое.
  host.addEventListener('click', function (e) {
    var tab = e.target.closest ? e.target.closest('.tab.on') : null;
    if (tab) e.preventDefault();
  });

  host.className = 'titlebar';
  host.innerHTML =
    '<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs>' +
      // Знак программы. Тот же контур, что в assets/tanba-mark.svg, откуда
      // собирается и значок exe; здесь он лежит заливкой, а не обводкой,
      // как остальные значки, и потому со своим классом.
      '<symbol id="t-logo" viewBox="1851 1859 6284 6284">' +
        '<polygon points="3646.41,3654.07 2748.76,3654.07 2748.76,2756.42 3646.41,2756.42 ' +
        '4544.05,2756.42 4544.05,3654.07 4544.05,8142.28 5441.69,8142.28 5441.69,3654.07 ' +
        '5441.69,2756.42 7236.98,2756.42 7236.98,3654.07 6339.33,3654.07 6339.33,4551.71 ' +
        '8134.62,4551.71 8134.62,1858.78 1851.12,1858.78 1851.12,4551.71 3646.41,4551.71"/>' +
      '</symbol>' +
      '<symbol id="t-inbox" viewBox="0 0 24 24"><path d="M22 12h-6l-2 3h-4l-2-3H2"/>' +
        '<path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89' +
        'A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/></symbol>' +
      '<symbol id="t-grid" viewBox="0 0 24 24"><rect width="7" height="7" x="3" y="3" rx="1"/>' +
        '<rect width="7" height="7" x="14" y="3" rx="1"/>' +
        '<rect width="7" height="7" x="14" y="14" rx="1"/>' +
        '<rect width="7" height="7" x="3" y="14" rx="1"/></symbol>' +
      '<symbol id="t-tag" viewBox="0 0 24 24"><path d="M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2' +
        'v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58' +
        'a2.426 2.426 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r=".5" fill="currentColor"/></symbol>' +
      '<symbol id="t-cog" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/>' +
        '<path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08' +
        'a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74' +
        'l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25' +
        'a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25' +
        'a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08' +
        'a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38' +
        'a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/></symbol>' +
      '<symbol id="v-s" viewBox="0 0 24 24">' +
        '<rect x="4" y="4" width="4" height="4" rx="1"/><rect x="10" y="4" width="4" height="4" rx="1"/>' +
        '<rect x="16" y="4" width="4" height="4" rx="1"/><rect x="4" y="10" width="4" height="4" rx="1"/>' +
        '<rect x="10" y="10" width="4" height="4" rx="1"/><rect x="16" y="10" width="4" height="4" rx="1"/>' +
        '<rect x="4" y="16" width="4" height="4" rx="1"/><rect x="10" y="16" width="4" height="4" rx="1"/>' +
        '<rect x="16" y="16" width="4" height="4" rx="1"/></symbol>' +
      '<symbol id="v-m" viewBox="0 0 24 24">' +
        '<rect x="4" y="4" width="7" height="7" rx="1"/><rect x="13" y="4" width="7" height="7" rx="1"/>' +
        '<rect x="4" y="13" width="7" height="7" rx="1"/><rect x="13" y="13" width="7" height="7" rx="1"/></symbol>' +
      '<symbol id="v-l" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" rx="2"/></symbol>' +
      '<symbol id="w-min" viewBox="0 0 24 24"><path d="M7 12h10"/></symbol>' +
      '<symbol id="w-max" viewBox="0 0 24 24"><rect x="7.5" y="7.5" width="9" height="9" rx="1.5"/></symbol>' +
      '<symbol id="w-rest" viewBox="0 0 24 24">' +
        '<rect x="6.5" y="9.5" width="8" height="8" rx="1.5"/>' +
        '<path d="M9.5 9.5v-1a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2h-1"/></symbol>' +
      '<symbol id="w-close" viewBox="0 0 24 24"><path d="m7.5 7.5 9 9m0-9-9 9"/></symbol>' +
    '</defs></svg>' +
    // Знак перед вкладками, на месте, которое всё равно пустовало. Он не
    // кнопка и никуда не ведёт, поэтому остаётся в области перетаскивания:
    // за него окно таскается так же, как за пустое место рядом.
    '<svg class="brand"><use href="#t-logo"></use></svg>' +
    '<nav class="tabs">' + tabs + '</nav>' +
    '<div class="tgrip"></div>' +
    '<div class="wbtns">' +
      '<button class="wbtn" data-win="min" data-i18n-title="win.minimize" title="Свернуть">' +
        '<svg class="ic"><use href="#w-min"></use></svg></button>' +
      '<button class="wbtn" data-win="max" data-i18n-title="win.maximize" title="Развернуть">' +
        '<svg class="ic"><use id="wMaxIcon" href="#w-max"></use></svg></button>' +
      '<button class="wbtn wclose" data-win="close" data-i18n-title="win.close" title="Закрыть">' +
        '<svg class="ic"><use href="#w-close"></use></svg></button>' +
    '</div>';

  // Шапку собрали с русским текстом, теперь заполняем по словарю. Дальше
  // за неё отвечает общий проход по странице: он идёт по всему документу
  // и после переключения языка забирает шапку вместе с остальным.
  I18N.apply(host);

  // Размер плиток. Класс вешаем на обёртку .files, а не на саму сетку:
  // сетку перерисовывают на каждое действие, обёртку нет, и выбранный
  // размер её переживает без всякой возни с восстановлением.
  var files = document.querySelector('.files');
  var seg = document.getElementById('tile');
  if (files && seg) {
    var setTile = function (size) {
      files.classList.remove('tile-s', 'tile-l');
      if (size === 's' || size === 'l') files.classList.add('tile-' + size);
      [].forEach.call(seg.children, function (b) { b.classList.toggle('on', b.dataset.tile === size); });
      localStorage.setItem('tanba-tile', size);
    };
    [].forEach.call(seg.children, function (b) {
      b.onclick = function () { setTile(b.dataset.tile); };
    });
    setTile(localStorage.getItem('tanba-tile') || 'm');
  }

  var bridge = window.chrome && window.chrome.webview;

  // В обычном браузере хоста нет, и кнопки окна там ничего не значат.
  if (!bridge) {
    host.querySelector('.wbtns').hidden = true;
    return;
  }

  host.querySelectorAll('[data-win]').forEach(function (b) {
    b.onclick = function () { bridge.postMessage({ kind: 'win.' + b.dataset.win }); };
  });

  // Кромку окна вокруг нашей шапки красит система, и цвет ей задаёт форма.
  // Про тему знает только страница, поэтому сообщаем сами.
  window.tanbaTheme = function () {
    bridge.postMessage({
      kind: 'theme',
      light: document.documentElement.dataset.tanba === 'light'
    });
  };
  window.tanbaTheme();

  // Окно забрано у системы целиком, поэтому его края накрыты страницей и
  // системе до них не дотянуться. Ловим их сами и просим Windows тянуть:
  // дальше она делает всё обычным образом, вместе с прилипанием и рамкой.
  var grips = document.createElement('div');
  grips.className = 'grips';
  grips.innerHTML = ['t', 'r', 'b', 'l', 'tl', 'tr', 'bl', 'br'].map(function (e) {
    return '<div class="grip g-' + e + '" data-edge="' + e + '"></div>';
  }).join('');
  document.body.appendChild(grips);

  // Перетаскивание ведём сами, от начала до конца. Отдать его системе нельзя:
  // мышь захвачена процессом WebView2, и системный цикл изменения размера
  // не получит ни движений, ни отпускания кнопки. Проверено, не работает.
  var drag = null;

  grips.addEventListener('pointerdown', function (ev) {
    if (ev.button !== 0) return;
    var g = ev.target.closest('[data-edge]');
    if (!g) return;
    ev.preventDefault();
    g.setPointerCapture(ev.pointerId);
    drag = {
      edge: g.dataset.edge,
      x: ev.screenX,
      y: ev.screenY,
      // Окно живёт в настоящих пикселях, а страница в своих: при масштабе
      // экрана 125% они расходятся, и окно тянулось бы медленнее мыши.
      k: window.devicePixelRatio || 1
    };
    bridge.postMessage({ kind: 'win.resize' });
  });

  grips.addEventListener('pointermove', function (ev) {
    if (!drag) return;
    bridge.postMessage({
      kind: 'win.sized',
      edge: drag.edge,
      dx: Math.round((ev.screenX - drag.x) * drag.k),
      dy: Math.round((ev.screenY - drag.y) * drag.k)
    });
  });

  function stopDrag() { drag = null; }
  grips.addEventListener('pointerup', stopDrag);
  grips.addEventListener('pointercancel', stopDrag);

  // Развёрнуто окно или нет, знает только форма: значок и подсказка
  // средней кнопки идут оттуда, а не угадываются страницей.
  bridge.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || m.kind !== 'winState') return;

    document.documentElement.classList.toggle('maxed', !!m.max);

    var use = document.getElementById('wMaxIcon');
    if (use) use.setAttribute('href', m.max ? '#w-rest' : '#w-max');

    // Меняем не саму подсказку, а ключ под ней: иначе переключение языка
    // вернуло бы кнопке текст из разметки, а он всегда про развернуть.
    var btn = host.querySelector('[data-win="max"]');
    if (btn) {
      btn.dataset.i18nTitle = m.max ? 'win.restore' : 'win.maximize';
      btn.title = t(btn.dataset.i18nTitle);
    }
  });
})();
