'use strict';

// Первый запуск. Шесть шагов в одном окне, состояние здесь, работа на сервере.

const $ = id => document.getElementById(id);
const bridge = window.chrome && window.chrome.webview;

// Шаги названы ключами, а не готовыми подписями: язык выбирают на первом же
// шаге, и подписи должны пересобраться под руками, а не остаться от загрузки.
const STEPS = ['setup.step.lang', 'setup.step.welcome', 'setup.step.place',
  'setup.step.launch', 'setup.step.work', 'common.done'];
const PANES = ['pLang', 'pWelcome', 'pPlace', 'pLaunch', 'pWork', 'pDone'];

// Шаги подготовки. Превью здесь нет намеренно: их рисует Windows по
// требованию, когда плитка попадает на экран, и шага такого не существует.
const TASKS = [
  { phase: 'dirs', key: 'setup.task.dirs' },
  { phase: 'db', key: 'setup.task.db' },
  { phase: 'scan', key: 'setup.task.scan' },
];

let step = 0;
let look = null;          // что сервер сказал про выбранную папку
let gone = false;         // прежний путь есть, а диска с ним нет
let lang = I18N.lang;
let version = '';
let shortcut = true;
let autostart = true;
let adopted = false;      // подключились к готовому каталогу
let poll = 0;

// Разметку заполняем сразу, до всякой сети: иначе окно на мгновение
// показывало бы русский тому, у кого выбран английский.
I18N.apply();

const post = async (url, body) => {
  const r = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  return { ok: r.ok, data: await r.json() };
};

// ── Ход по шагам ───────────────────────────────────────────────────────

function show(n) {
  step = n;
  PANES.forEach((id, i) => $(id).hidden = i !== n);

  $('dots').innerHTML = STEPS
    .map((_, i) => `<i class="${i === n ? 'now' : i < n ? 'done' : ''}"></i>`).join('');
  $('stepLbl').textContent =
    t('setup.step.counter', { n: n + 1, total: STEPS.length, name: t(STEPS[n]) });

  // Назад некуда с первого шага и незачем с последнего, а во время работы
  // возвращаться нельзя: она уже идёт.
  $('back').hidden = n === 0 || n >= 4;

  const next = $('next');
  next.textContent = [t('setup.nav.next'), t('setup.nav.start'),
    adopted ? t('setup.nav.connect') : t('setup.nav.next'), t('setup.nav.prepare'),
    t('setup.nav.working'), t('setup.nav.open')][n];
  next.disabled = (n === 2 && (gone || !look || look.kind === 'bad')) || n === 4;

  if (n === 3) paintLaunch();
  if (n === 4) startWork();
}

$('back').onclick = () => show(step - 1);

$('next').onclick = async () => {
  if (step === 2) {
    // Подготовку запускаем сразу: шаг «Готовлю» должен открыться уже работающим.
    show(3);
    return;
  }
  if (step === 3) { show(4); return; }
  if (step === 5) {
    await post('/api/setup/finish', { path: look.path });
    return;
  }
  show(step + 1);
};

// ── Шаг 1: язык ────────────────────────────────────────────────────────

$('langRu').onclick = () => pick('ru');
$('langEn').onclick = () => pick('en');

/// <summary>
/// Выбранный язык. Мастер переключается тут же, на том же шаге: спросить
/// на языке, которого человек не знает, и остаться на нём после ответа
/// одинаково нехорошо.
/// </summary>
function pick(v) {
  // Выбор уходит в localStorage, и главное окно откроется на том же языке,
  // никого не спрашивая. В настройки, то есть в базу, он уходит отдельно,
  // на шаге «Готовлю»: базы пока нет, писать некуда.
  lang = I18N.set(v);

  $('langRu').classList.toggle('on', lang === 'ru');
  $('langEn').classList.toggle('on', lang === 'en');

  // Всё, что собрано кодом, разметочный проход не трогает: подписи шагов,
  // кнопки и приговор папке перерисовываем сами.
  $('ver').textContent = t('setup.welcome.version', { v: version });
  if (look) paintLook();
  show(step);
}

// ── Шаг 3: где держать архив ───────────────────────────────────────────

let typing = 0;
$('path').addEventListener('input', () => {
  clearTimeout(typing);
  typing = setTimeout(ask, 350);
});

$('browse').onclick = () => bridge && bridge.postMessage({ kind: 'browse' });
if (bridge) {
  bridge.addEventListener('message', e => {
    if (e.data && e.data.kind === 'folder') { $('path').value = e.data.path; ask(); }
  });
}

async function ask() {
  const path = $('path').value.trim();
  gone = false;
  if (!path) {
    look = null;
    $('verdict').hidden = true;
    $('willmake').hidden = true;
    $('pathBox').className = 'pathbox';
    if (step === 2) $('next').disabled = true;
    return;
  }

  const { data } = await post('/api/setup/look', { path });
  look = data;
  adopted = data.kind === 'tanba';
  paintLook();
}

/// <summary>
/// Приговор папке. Рисуется отдельно от вопроса серверу: тот же вид нужен
/// ещё раз после переключения языка, а спрашивать заново там нечего.
/// </summary>
function paintLook() {
  const data = look;

  const tone = gone ? 'bad'
    : { empty: 'ok', tanba: 'ok', foreign: 'warn', bad: 'bad' }[data.kind] || '';
  $('pathBox').className = 'pathbox ' + tone;

  $('verdict').hidden = false;
  $('verdict').className = 'verdict ' + (gone ? 'bad' : data.kind);
  $('vmark').innerHTML = `<svg class="ic"><use href="#${
    gone ? 'i-disk'
    : data.kind === 'tanba' ? 'i-check'
    : data.kind === 'empty' ? 'i-folder' : 'i-warn'}"></use></svg>`;

  // Строка про чужую папку и про свой каталог наша, а про беду с путём
  // говорит сервер: он один знает, что именно не вышло.
  $('vtext').textContent =
    gone ? t('setup.verdict.gone')
    : data.kind === 'tanba'
      ? t('setup.verdict.tanba', { files: tn(data.files, 'setup.plural.files') })
    : data.kind === 'empty' ? t('setup.verdict.empty')
    : data.kind === 'foreign' ? t('setup.verdict.foreign')
    : data.note;

  $('vmeta').textContent = gone ? ''
    : data.kind === 'tanba' && data.created ? t('setup.verdict.created', { date: data.created })
    : data.kind === 'foreign'
      ? t('setup.verdict.items', { n: I18N.num(data.items) + (data.items >= 50 ? '+' : '') })
    : '';

  // Что именно появится, показываем только там, где это кого-то встревожит.
  $('willmake').hidden = data.kind !== 'foreign';
  $('placeSub').textContent = data.kind === 'tanba'
    ? t('setup.place.sub.tanba')
    : t('setup.place.sub');

  if (step === 2) {
    $('next').disabled = gone || data.kind === 'bad';
    $('next').textContent = adopted ? t('setup.nav.connect') : t('setup.nav.next');
  }
}

// ── Шаг 4: как запускать ───────────────────────────────────────────────

$('optShortcut').onclick = () => { shortcut = !shortcut; paintLaunch(); };
$('optAutostart').onclick = () => { autostart = !autostart; paintLaunch(); };

function paintLaunch() {
  $('optShortcut').classList.toggle('on', shortcut);
  $('optAutostart').classList.toggle('on', autostart);
}

// ── Шаг 5: готовлю ─────────────────────────────────────────────────────

async function startWork() {
  $('workTitle').textContent = adopted ? t('setup.work.title.adopt') : t('setup.work.title');
  $('workSub').textContent = adopted ? t('setup.work.sub.adopt') : t('setup.work.sub');

  // Язык уходит вместе с остальным: настройки заводятся здесь же, и без
  // него сервер взял бы язык Windows, а не тот, что выбрали на первом шаге.
  const { ok, data } = await post('/api/setup/prepare',
    { path: look.path, shortcut, autostart, lang });
  if (!ok) { fail(data.note || t('setup.work.failstart')); return; }

  clearInterval(poll);
  poll = setInterval(tick, 300);
  tick();
}

async function tick() {
  let p;
  try { p = await (await fetch('/api/setup/progress')).json(); }
  catch (e) { return; }

  if (p.failed) { clearInterval(poll); fail(p.failed); return; }

  const at = TASKS.findIndex(x => x.phase === p.phase);
  const done = p.phase === 'done' || p.phase === 'extras';

  // Доля считается по шагам, а обход внутри своего шага по файлам: иначе
  // полоса стоит на месте всё то время, пока считаются отпечатки.
  const units = p.adopted ? 1 : 3;
  const passed = done ? units
    : (p.adopted ? 0 : Math.max(0, at)) + (p.phase === 'scan' && p.total ? p.scanned / p.total : 0);
  const pct = Math.min(100, Math.round(passed / units * 100));
  $('pfill').style.width = pct + '%';
  $('progPct').textContent = pct + '%';

  // Строка говорит, что идёт прямо сейчас, и на обходе добавляет счёт:
  // без него полоса ползёт молча и непонятно, сколько ещё ждать.
  $('progLabel').textContent = done ? t('common.done')
    : p.phase === 'scan' && p.total
      ? t('setup.prog.counted',
          { task: t(TASKS[at].key), done: I18N.num(p.scanned), total: I18N.num(p.total) })
    : TASKS[at] ? t(TASKS[at].key) : t('setup.prog.starting');

  if (p.phase === 'done') {
    clearInterval(poll);
    $('doneWhere').textContent = look.path.replace(/\\?$/, '\\') + 'Inbox';
    show(5);
  }
}

function fail(text) {
  $('workTitle').textContent = t('setup.fail.title');
  $('workSub').textContent = text;
  $('next').disabled = false;
  $('next').textContent = t('setup.fail.back');
  $('next').onclick = () => { $('next').onclick = null; location.reload(); };
}

// ── Начало ─────────────────────────────────────────────────────────────

(async () => {
  const s = await (await fetch('/api/setup')).json();
  shortcut = s.shortcut;
  autostart = s.autostart;
  version = s.version || '';

  // Каким языком открыться, говорит сервер: базы ещё нет, и он берёт язык
  // Windows. Прежний выбор, если человек тут уже был, сильнее: его сделали
  // руками, а язык системы всего лишь угадан.
  pick(localStorage.getItem('tanba-lang') || s.lang);

  if (s.current) {
    $('path').value = s.current;
    await ask();
    if (s.waiting) {
      // Диск не подключён: приветствие человеку не нужно, он уже здесь был.
      gone = true;
      paintLook();
      show(2);
      return;
    }
  } else if (s.found && s.found.length === 1) {
    // Нашёлся ровно один каталог: почти наверняка тот самый, подставляем.
    // Списка найденного на экране нет, и не нужен: путь уже в поле, а что
    // в нём лежит, сказано строкой ниже.
    $('path').value = s.found[0].path;
    await ask();
  }

  show(0);
})();

const esc = s => String(s).replace(/[&<>"]/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
