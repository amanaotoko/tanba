'use strict';

// Настройки: версия и обновление, запуск вместе с Windows, хранилище.
// Обновление здесь только по кнопке. Тихое обновление посреди работы
// закрыло бы окно ровно тогда, когда размечается пачка файлов.

let s = null;          // последний снимок с сервера
let installing = false;

const $ = id => document.getElementById(id);

const send = async (method, url, body) => {
  const r = await fetch(url, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
};

function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
}

const num = n => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');

// ── Отрисовка ───────────────────────────────────────────────────────────

async function load() {
  s = await send('GET', '/api/settings');
  render();
}

function render() {
  const u = s.update;

  $('ver').textContent = s.version;
  $('root').textContent = s.root;
  $('rootNote').textContent = s.scanning
    ? 'Считаю, что лежит в приёме'
    : `В приёме ${num(s.pending)} ${plural(s.pending, 'файл', 'файла', 'файлов')}`;

  // Состояние обновления одной строкой: она же несёт ошибку, если та случилась.
  const st = $('upState');
  st.className = 'rd';
  $('apply').hidden = true;
  $('check').disabled = false;

  if (installing) {
    st.textContent = 'Устанавливаю, окно сейчас закроется и откроется заново';
    st.classList.add('wait');
    $('check').disabled = true;
  } else if (u.busy) {
    st.textContent = 'Смотрю…';
    st.classList.add('wait');
    $('check').disabled = true;
  } else if (u.error) {
    st.textContent = u.error;
    st.classList.add('err');
  } else if (u.available) {
    st.textContent = `Есть версия ${u.available}`;
    st.classList.add('ok');
    $('apply').hidden = false;
  } else if (!u.configured) {
    st.textContent = 'Источник обновлений не указан';
  } else if (u.checkedAt) {
    st.textContent = `Последняя проверка: ${when(u.checkedAt)}. Установлена самая свежая.`;
  } else {
    st.textContent = 'Ещё не проверяли';
  }

  // Источник
  for (const b of $('src').children) b.classList.toggle('on', b.dataset.src === u.source);
  $('rowRepo').hidden = u.source !== 'github';
  $('rowFolder').hidden = u.source !== 'folder';

  // Адрес и ключ доступа вшиты при сборке, поэтому здесь их только показываем.
  $('repo').textContent = u.repo || 'репозиторий не задан при сборке';
  if (document.activeElement !== $('folder')) $('folder').value = u.folder;

  // Запуск
  $('startup').classList.toggle('on', s.startup);
  $('rowTarget').hidden = !s.startup;
  $('target').textContent = s.startupTarget;

  $('stLeft').textContent = u.installed
    ? 'Установленная копия'
    : 'Запущена не из установленной копии, обновление недоступно';
  $('stRight').textContent = `Tanba ${s.version}`;
}

/// Дата проверки словами: «сегодня в 14:22» читается быстрее, чем 2026-08-09.
function when(unix) {
  const d = new Date(unix * 1000);
  const t = d.toLocaleTimeString('ru', { hour: '2-digit', minute: '2-digit' });
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  if (diff === 0) return `сегодня в ${t}`;
  if (diff === 1) return `вчера в ${t}`;
  return d.toLocaleDateString('ru', { day: 'numeric', month: 'long' }) + ` в ${t}`;
}

// ── Обновление ──────────────────────────────────────────────────────────

$('check').onclick = async () => {
  $('check').disabled = true;
  try { s = await send('POST', '/api/update/check'); render(); }
  catch (e) { toast(String(e.message || e), 'err'); $('check').disabled = false; }
};

$('apply').onclick = async () => {
  installing = true;
  render();
  try { await send('POST', '/api/update/apply'); } catch (e) { /* процесс мог уже уйти */ }
  watchInstall();
};

// Установка заменяет процесс целиком, вместе с этим окном. Поэтому пока
// сервер отвечает, обновление либо качается, либо не задалось, и разницу
// показывает поле ошибки.
function watchInstall() {
  const timer = setInterval(async () => {
    try {
      const next = await send('GET', '/api/settings');
      if (next.update.error) {
        clearInterval(timer);
        installing = false;
        s = next;
        render();
      }
    } catch (e) {
      clearInterval(timer);
      waitForNewVersion();
    }
  }, 1500);
}

/// Соединение оборвалось. Само по себе это ничего не доказывает: так же
/// выглядит и упавшая программа, а раньше мы в обоих случаях писали
/// «обновляюсь» и оставляли человека перед надписью, которая уже никогда
/// не сменится. Успех здесь один: по тому же адресу отзовётся новая версия.
function waitForNewVersion() {
  say('Обновляюсь', 'Окно откроется заново само.');

  let left = 60;
  const back = setInterval(async () => {
    try {
      await send('GET', '/api/settings');
      clearInterval(back);
      location.reload();
    } catch (e) {
      if (--left > 0) return;
      clearInterval(back);
      say('Обновление не установилось',
          'Программа так и не отозвалась. Закрой окно и запусти Tanba заново.');
    }
  }, 1000);
}

/// Заглушка на весь экран: заголовок и строка под ним.
function say(head, note) {
  const box = document.createElement('div');
  box.className = 'empty';
  const h = document.createElement('h2');
  h.textContent = head;
  const p = document.createElement('p');
  p.textContent = note;
  box.append(h, p);
  document.body.replaceChildren(box);
}

// ── Источник ────────────────────────────────────────────────────────────

for (const b of $('src').children) {
  b.onclick = async () => { s = await send('POST', '/api/update/source', { source: b.dataset.src }); render(); };
}

const folder = $('folder');
folder.onblur = async () => {
  if (folder.value.trim() === s.update.folder) return;
  s = await send('POST', '/api/update/source', { folder: folder.value.trim() });
  render();
  toast('Сохранено');
};
folder.onkeydown = e => { if (e.key === 'Enter') folder.blur(); };

// ── Запуск вместе с Windows ─────────────────────────────────────────────

$('startup').onclick = async () => {
  const sw = $('startup');
  if (sw.classList.contains('busy')) return;
  sw.classList.add('busy');
  try {
    const r = await send('POST', '/api/startup', { on: !s.startup });
    s.startup = r.on;
    render();
    // Отвечаем тем, что получилось, а не тем, о чём просили: запись в реестр
    // мог запретить администратор, и молчаливое «включено» было бы враньём.
    toast(r.on ? 'Tanba будет запускаться вместе с Windows' : 'Автозапуск выключен');
  } catch (e) { toast(String(e.message || e), 'err'); }
  finally { sw.classList.remove('busy'); }
};

$('openRoot').onclick = () => send('POST', '/api/openroot').catch(() => toast('Не открылось', 'err'));

// ── Мелочи ──────────────────────────────────────────────────────────────

let toastTimer;
function toast(text, cls = '') {
  const el = $('toast');
  el.textContent = text;
  el.className = 'toast show ' + cls;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.className = 'toast ' + cls, 3000);
}

// Единственный переключатель темы на всю программу: раньше он стоял в шапке
// каждого экрана, а шапку заняли вкладки.
for (const b of $('theme').children) {
  b.onclick = () => {
    document.documentElement.dataset.tanba = b.dataset.theme;
    localStorage.setItem('tanba-theme', b.dataset.theme);
    paintTheme();
    // Кромка окна принадлежит системе, её перекрашивает форма.
    if (window.tanbaTheme) window.tanbaTheme();
  };
}

function paintTheme() {
  const now = document.documentElement.dataset.tanba === 'light' ? 'light' : 'dark';
  for (const b of $('theme').children) b.classList.toggle('on', b.dataset.theme === now);
}
paintTheme();

load();
