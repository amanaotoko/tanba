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
    ? t('set.root.scanning')
    : tn(s.pending, 'set.root.pending');

  // Состояние обновления одной строкой: она же несёт ошибку, если та случилась.
  const st = $('upState');
  st.className = 'rd';
  $('apply').hidden = true;
  $('check').disabled = false;

  if (installing) {
    st.textContent = t('set.up.installing');
    st.classList.add('wait');
    $('check').disabled = true;
  } else if (u.busy) {
    st.textContent = t('set.up.checking');
    st.classList.add('wait');
    $('check').disabled = true;
  } else if (u.error) {
    st.textContent = u.error;
    st.classList.add('err');
  } else if (u.available) {
    st.textContent = t('set.up.available', { ver: u.available });
    st.classList.add('ok');
    $('apply').hidden = false;
  } else if (!u.configured) {
    st.textContent = t('set.up.noSource');
  } else if (u.checkedAt) {
    st.textContent = t('set.up.upToDate', { when: when(u.checkedAt) });
  } else {
    st.textContent = t('set.up.never');
  }

  // Адрес и ключ доступа вшиты при сборке, поэтому здесь их только показываем.
  $('repo').textContent = u.repo || t('set.up.noRepo');

  // Запуск
  $('startup').classList.toggle('on', s.startup);
  $('rowTarget').hidden = !s.startup;
  $('target').textContent = s.startupTarget;

  $('stLeft').textContent = u.installed
    ? t('set.up.installed')
    : t('set.up.portable');
  $('stRight').textContent = `Tanba ${s.version}`;
}

/// Дата проверки словами: «сегодня в 14:22» читается быстрее, чем 2026-08-09.
function when(unix) {
  const time = I18N.time(unix);
  const day = new Date(unix * 1000); day.setHours(0, 0, 0, 0);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  if (diff === 0) return t('set.when.today', { time });
  if (diff === 1) return t('set.when.yesterday', { time });
  return t('set.when.onDate', { date: I18N.date(unix), time });
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
  let misses = 0;
  const timer = setInterval(async () => {
    try {
      const next = await send('GET', '/api/settings');
      misses = 0;
      if (next.update.error) {
        clearInterval(timer);
        installing = false;
        s = next;
        render();
      }
    } catch (e) {
      // Один неответ ничего не доказывает: страницу мог усыпить WebView2,
      // а сервер мог не успеть ответить за полтора секунды на середине
      // распаковки. Считаем ушедшим после трёх подряд.
      if (++misses < 3) return;
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
  say(t('set.up.updating.title'), t('set.up.updating.body'));

  let left = 60;
  const back = setInterval(async () => {
    try {
      await send('GET', '/api/settings');
      clearInterval(back);
      location.reload();
    } catch (e) {
      if (--left > 0) return;
      clearInterval(back);
      say(t('set.up.failed.title'), t('set.up.failed.body'));
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
    toast(r.on ? t('set.startup.on') : t('set.startup.off'));
  } catch (e) { toast(String(e.message || e), 'err'); }
  finally { sw.classList.remove('busy'); }
};

$('openRoot').onclick = () => send('POST', '/api/openroot').catch(() => toast(t('toast.openFailedShort'), 'err'));

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

// Единственный переключатель языка, по тем же причинам, что и у темы.
for (const b of $('lang').children) {
  b.onclick = () => {
    const v = b.dataset.lang;
    // Разметку переписывает apply, а тексты, которые собирает код, render:
    // на экране есть и то, и другое.
    I18N.set(v);
    I18N.apply();
    paintLang();
    if (s) render();   // до первого ответа сервера рисовать ещё нечего
    // Настоящее место выбора это настройки программы, а не localStorage:
    // значок в трее и мастер первого запуска берут язык оттуда.
    send('POST', '/api/lang', { lang: v }).catch(e => toast(String(e.message || e), 'err'));
  };
}

function paintLang() {
  for (const b of $('lang').children) b.classList.toggle('on', b.dataset.lang === I18N.lang);
}

I18N.apply();

// Быстрая копия языка могла разойтись с настоящей настройкой: её выбирают
// в мастере, а у того своё окно и свой localStorage. Сверяемся и, если
// разошлись, перерисовываемся уже на верном языке.
I18N.sync(() => { paintLang(); render(); });
paintTheme();
paintLang();

load();
