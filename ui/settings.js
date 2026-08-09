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

// Установка завершает процесс, поэтому признак успеха здесь это оборвавшееся
// соединение. Пока сервер отвечает, обновление либо качается, либо не задалось.
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
      document.body.innerHTML =
        '<div class="empty"><h2>Обновляюсь</h2><p>Окно откроется заново само.</p></div>';
    }
  }, 1500);
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

$('theme').onclick = () => {
  const light = document.documentElement.dataset.tanba === 'light';
  document.documentElement.dataset.tanba = light ? 'dark' : 'light';
  $('theme').querySelector('use').setAttribute('href', light ? '#i-sun' : '#i-moon');
  localStorage.setItem('tanba-theme', light ? 'dark' : 'light');
};
if (localStorage.getItem('tanba-theme') === 'light') {
  document.documentElement.dataset.tanba = 'light';
  $('theme').querySelector('use').setAttribute('href', '#i-moon');
}

load();
