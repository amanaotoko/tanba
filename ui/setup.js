'use strict';

// Первый запуск. Пять шагов в одном окне, состояние здесь, работа на сервере.

const $ = id => document.getElementById(id);
const bridge = window.chrome && window.chrome.webview;

const STEPS = ['Приветствие', 'Где держать архив', 'Как запускать', 'Готовлю', 'Готово'];
const PANES = ['pWelcome', 'pPlace', 'pLaunch', 'pWork', 'pDone'];

// Шаги подготовки. Превью здесь нет намеренно: их рисует Windows по
// требованию, когда плитка попадает на экран, и шага такого не существует.
const TASKS = [
  { phase: 'dirs', text: 'Создаю папки' },
  { phase: 'db', text: 'Завожу базу' },
  { phase: 'scan', text: 'Смотрю, что уже лежит в архиве' },
];

let step = 0;
let look = null;          // что сервер сказал про выбранную папку
let shortcut = true;
let autostart = true;
let adopted = false;      // подключились к готовому каталогу
let poll = 0;

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
  $('stepLbl').textContent = `Шаг ${n + 1} из ${STEPS.length}, ${STEPS[n]}`;

  // Назад некуда с первого шага и незачем с последнего, а во время работы
  // возвращаться нельзя: она уже идёт.
  $('back').hidden = n === 0 || n >= 3;

  const next = $('next');
  next.textContent = ['Начать', adopted ? 'Подключиться' : 'Далее',
    'Готовить', 'Идёт подготовка', 'Открыть Tanba'][n];
  next.disabled = (n === 1 && (!look || look.kind === 'bad')) || n === 3;

  if (n === 2) paintLaunch();
  if (n === 3) startWork();
}

$('back').onclick = () => show(step - 1);

$('next').onclick = async () => {
  if (step === 1) {
    // Подготовку запускаем сразу: шаг «Готовлю» должен открыться уже работающим.
    show(2);
    return;
  }
  if (step === 2) { show(3); return; }
  if (step === 4) {
    await post('/api/setup/finish', { path: look.path });
    return;
  }
  show(step + 1);
};

// ── Шаг 2: где держать архив ───────────────────────────────────────────

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
  if (!path) {
    look = null;
    $('verdict').hidden = true;
    $('willmake').hidden = true;
    $('pathBox').className = 'pathbox';
    if (step === 1) $('next').disabled = true;
    return;
  }

  const { data } = await post('/api/setup/look', { path });
  look = data;
  adopted = data.kind === 'tanba';

  const tone = { empty: 'ok', tanba: 'ok', foreign: 'warn', bad: 'bad' }[data.kind] || '';
  $('pathBox').className = 'pathbox ' + tone;

  $('verdict').hidden = false;
  $('verdict').className = 'verdict ' + data.kind;
  $('vmark').innerHTML = `<svg class="ic"><use href="#${
    data.kind === 'tanba' ? 'i-check' : data.kind === 'empty' ? 'i-folder' : 'i-warn'}"></use></svg>`;

  $('vtext').textContent =
    data.kind === 'tanba' ? `Каталог на ${data.files} ${plural(data.files)}, подключусь к нему`
    : data.kind === 'empty' ? 'Заведу здесь всё с нуля'
    : data.kind === 'foreign' ? 'Папка не пуста, свои папки создам рядом'
    : data.note;

  $('vmeta').textContent =
    data.kind === 'tanba' && data.created ? 'создан ' + data.created
    : data.kind === 'foreign' ? `${data.items}${data.items >= 50 ? '+' : ''} внутри`
    : '';

  // Что именно появится, показываем только там, где это кого-то встревожит.
  $('willmake').hidden = data.kind !== 'foreign';
  $('placeSub').textContent = data.kind === 'tanba'
    ? 'Нашёлся прежний каталог. Подключусь к нему, ничего не перезаписывая.'
    : 'Файлы остаются на диске. Tanba создаст рядом служебные папки и базу с тегами.';

  if (step === 1) {
    $('next').disabled = data.kind === 'bad';
    $('next').textContent = adopted ? 'Подключиться' : 'Далее';
  }
}

// ── Шаг 3: как запускать ───────────────────────────────────────────────

$('optShortcut').onclick = () => { shortcut = !shortcut; paintLaunch(); };
$('optAutostart').onclick = () => { autostart = !autostart; paintLaunch(); };

function paintLaunch() {
  $('optShortcut').classList.toggle('on', shortcut);
  $('optAutostart').classList.toggle('on', autostart);
}

// ── Шаг 4: готовлю ─────────────────────────────────────────────────────

async function startWork() {
  $('workTitle').textContent = adopted ? 'Подключаюсь к каталогу' : 'Готовлю каталог';
  $('workSub').textContent = adopted
    ? 'Папки и база уже есть, смотрю, что в них.'
    : 'Создаю служебные папки и смотрю, что уже лежит в архиве.';

  const { ok, data } = await post('/api/setup/prepare',
    { path: look.path, shortcut, autostart });
  if (!ok) { fail(data.note || 'Не вышло начать.'); return; }

  clearInterval(poll);
  poll = setInterval(tick, 300);
  tick();
}

async function tick() {
  let p;
  try { p = await (await fetch('/api/setup/progress')).json(); }
  catch (e) { return; }

  if (p.failed) { clearInterval(poll); fail(p.failed); return; }

  const at = TASKS.findIndex(t => t.phase === p.phase);
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
  $('progLabel').textContent = done ? 'Готово'
    : p.phase === 'scan' && p.total ? `${TASKS[at].text}, ${p.scanned} из ${p.total}`
    : (TASKS[at]?.text || 'Начинаю');

  if (p.phase === 'done') {
    clearInterval(poll);
    $('doneWhere').textContent = look.path.replace(/\\?$/, '\\') + 'Inbox';
    show(4);
  }
}

function fail(text) {
  $('workTitle').textContent = 'Не получилось';
  $('workSub').textContent = text;
  $('next').disabled = false;
  $('next').textContent = 'Назад к выбору';
  $('next').onclick = () => { $('next').onclick = null; location.reload(); };
}

// ── Начало ─────────────────────────────────────────────────────────────

(async () => {
  const s = await (await fetch('/api/setup')).json();
  shortcut = s.shortcut;
  autostart = s.autostart;
  $('ver').textContent = 'версия ' + (s.version || '');

  if (s.current) {
    $('path').value = s.current;
    await ask();
    if (s.waiting) {
      // Диск не подключён: приветствие человеку не нужно, он уже здесь был.
      $('verdict').hidden = false;
      $('verdict').className = 'verdict bad';
      $('vmark').innerHTML = `<svg class="ic"><use href="#i-disk"></use></svg>`;
      $('vtext').textContent = 'Прежний путь недоступен, подключи диск или выбери другое место';
      $('vmeta').textContent = '';
      show(1);
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

function plural(n) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return 'файлов';
  if (b > 1 && b < 5) return 'файла';
  if (b === 1) return 'файл';
  return 'файлов';
}
