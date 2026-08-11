'use strict';

// Язык интерфейса. Словарь лежит рядом, в strings.js: здесь только механика.
//
// Выбранный язык читается из localStorage сразу, до отрисовки, потому что
// иначе окно на мгновение показывало бы русский и переключалось на английский
// у всех, кто выбрал английский. Тем же способом здесь же живёт тема, см.
// chrome.js: этот файл тоже подключён ко всем экранам.
//
// Настоящее место хранения выбора это настройки программы, то есть база:
// её читает не только окно, но и значок в трее, который никакого localStorage
// не видит. Здесь лежит быстрая копия, а расходятся они только в одном месте,
// на экране настроек, который пишет обе.
//
// Теги сюда не относятся ни в каком виде. Имена тегов, групп и файлов
// приходят из базы как есть и через словарь не проходят: это данные человека.

const I18N = (function () {
  const KEY = 'tanba-lang';

  function guess() {
    const saved = localStorage.getItem(KEY);
    if (saved === 'ru' || saved === 'en') return saved;
    return (navigator.language || 'ru').slice(0, 2) === 'ru' ? 'ru' : 'en';
  }

  let lang = guess();

  /// Локаль для дат и чисел. Русский и английский расходятся во всём:
  /// порядок дня и месяца, разделитель тысяч, двенадцатичасовое время.
  const locale = () => (lang === 'ru' ? 'ru-RU' : 'en-US');

  /// <summary>Строка по ключу. Подстановки в фигурных скобках: {n}, {path}.</summary>
  function t(key, vars) {
    const table = STRINGS[lang] || STRINGS.ru;
    let s = table[key];

    // Ключа нет в переводе: берём русский, чтобы человек увидел текст,
    // а не служебное имя. Само имя уходит в консоль для того, кто чинит.
    if (s === undefined) {
      s = STRINGS.ru[key];
      if (s === undefined) { console.warn('нет строки:', key); return key; }
    }

    if (!vars) return s;
    return s.replace(/\{(\w+)\}/g, (m, name) =>
      Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : m);
  }

  /// <summary>
  /// Число со словом: 5 файлов, 5 files. Русский требует три формы, и правило
  /// у него своё, поэтому выбирает его язык, а не место вызова.
  /// </summary>
  function plural(n, key, vars) {
    const forms = (STRINGS[lang] || STRINGS.ru)[key] || STRINGS.ru[key];
    if (!Array.isArray(forms)) { console.warn('нет форм:', key); return String(n); }

    let form;
    if (lang === 'ru') {
      const a = Math.abs(n) % 100, b = a % 10;
      form = (a > 10 && a < 20) ? forms[2]
           : (b > 1 && b < 5) ? forms[1]
           : (b === 1) ? forms[0]
           : forms[2];
    } else {
      form = Math.abs(n) === 1 ? forms[0] : forms[1];
    }

    return form.replace(/\{n\}/g, num(n)).replace(/\{(\w+)\}/g, (m, name) =>
      vars && Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : m);
  }

  /// Число с разделителем тысяч: 1 234 по-русски, 1,234 по-английски.
  const num = n => (Number(n) || 0).toLocaleString(locale());

  /// Время и дата одной парой, чтобы они не разъезжались по экранам.
  const time = sec =>
    new Date(sec * 1000).toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' });

  const date = sec =>
    new Date(sec * 1000).toLocaleDateString(locale(), { day: 'numeric', month: 'long', year: 'numeric' });

  /// <summary>
  /// Сравнение имён для сортировки. Нужно потому, что база сортирует байтами:
  /// вся латиница идёт раньше всей кириллицы, и стоит завести теги на двух
  /// языках, как список выглядит перемешанным. Сортируем на своей стороне.
  /// </summary>
  const collator = () => new Intl.Collator(locale(), { numeric: true, sensitivity: 'base' });

  const byName = (a, b) => collator().compare(a, b);

  /// <summary>
  /// Проставляет тексты по разметке. Ищет data-i18n и его же для атрибутов,
  /// чтобы одну строку можно было положить и в текст, и в подсказку.
  /// </summary>
  function apply(root) {
    const where = root || document;

    for (const el of where.querySelectorAll('[data-i18n]'))
      el.textContent = t(el.dataset.i18n);

    for (const [attr, data] of [
      ['title', 'i18nTitle'],
      ['placeholder', 'i18nPlaceholder'],
      ['aria-label', 'i18nAria'],
    ])
      for (const el of where.querySelectorAll(`[data-${attr === 'aria-label' ? 'i18n-aria' : 'i18n-' + attr}]`))
        el.setAttribute(attr, t(el.dataset[data]));

    // Язык страницы: по нему работает проверка орфографии в полях
    // переименования и в редакторе тегов.
    document.documentElement.lang = lang;
  }

  function set(v) {
    if (v !== 'ru' && v !== 'en') return lang;
    lang = v;
    localStorage.setItem(KEY, v);
    apply();
    return lang;
  }

  return { t, plural, num, time, date, collator, byName, apply, set, locale, get lang() { return lang; } };
})();

const t = I18N.t;
const tn = I18N.plural;
