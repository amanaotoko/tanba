'use strict';

// Командная панель и панель сведений. Один файл на два экрана: разбор и
// библиотека показывают одни и те же файлы и делают с ними одно и то же,
// и если развести это по экранам, они разойдутся в поведении на первой же
// правке. Экран сообщает о себе через window.tanbaCmd, см. ниже.
//
// Действия те же самые, что в правом меню библиотеки, и это не совпадение:
// меню теперь зовёт отсюда, чтобы кнопка и пункт меню не могли начать
// вести себя по-разному.

// ── Что экран обязан о себе рассказать ─────────────────────────────────
//
//   selected()    id выделенных объектов
//   byId(id)      строка объекта: name, ext, size, addedAt, tags, kind
//   reload()      перечитать экран после изменения
//   tagName(id)   имя тега по номеру, для панели сведений
//   enter(id)     войти в каталог, если экран это умеет
//   delCatalog(f) удалить каталог, если экран это умеет
//
// Пусто, пока экран не представился: тогда панель просто ничего не делает.
const cmd = () => window.tanbaCmd || {};

const cbar = document.querySelector('.cmd');
const cinfo = document.getElementById('info');
const cmain = document.getElementById('main');

// ── Свои запросы ───────────────────────────────────────────────────────
// Библиотека и разбор ходят на сервер по-разному, jget с одной стороны и
// api с другой. Свой маленький вместо того, чтобы зависеть от экрана.

async function csend(url, method, body) {
  const r = await fetch(url, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.status === 204 ? null : r.json();
}

// ── Действия ───────────────────────────────────────────────────────────

async function cmdAct(act) {
  const s = cmd();
  const list = (s.selected && s.selected()) || [];
  if (!list.length) return;
  const one = list.length === 1 ? s.byId?.(list[0]) : null;

  try {
    if (act === 'open') {
      // Каталог открывается входом внутрь, а не системой: своих байтов
      // у него нет и открывать нечего.
      if (one && one.kind === 'catalog') return s.enter?.(one.id);
      for (const id of list) await csend('/api/files/open', 'POST', { id });
      return;
    }
    if (act === 'rename') return one && cmdRename(one);
    if (act === 'tags') return cmdTags(list);
    if (act === 'del') {
      if (one && one.kind === 'catalog') return s.delCatalog?.(one);
      return cmdDelete(list, one);
    }
  } catch (e) {
    toast('Не вышло: ' + e.message, 'err');
  }
}

function cmdRename(file) {
  const wrap = modalBox(`
    <h2>Переименовать</h2>
    <p>Расширение остаётся прежним, менять его вручную не нужно.</p>
    <input class="name" value="${esc(file.name.replace(/\.[^.]+$/, ''))}" spellcheck="false">
    <div class="modal-acts">
      <button class="btn" data-no>Отмена</button>
      <button class="btn btn-primary" data-yes>Переименовать</button>
    </div>`);

  const input = wrap.querySelector('.name');
  const shut = () => { wrap.remove(); document.removeEventListener('keydown', bye); };
  const bye = e => { if (e.key === 'Escape') shut(); };

  const save = async () => {
    const nm = input.value.trim();
    if (!nm) return shut();
    try {
      file.kind === 'catalog'
        ? await csend('/api/catalogs/' + file.id, 'PATCH', { name: nm })
        : await csend('/api/files/rename', 'POST', { id: file.id, name: nm });
      shut();
      await cmd().reload?.();
      toast('Переименовано');
    } catch (e) { toast('Не вышло: ' + e.message, 'err'); }
  };

  input.onkeydown = e => { if (e.key === 'Enter') save(); };
  wrap.querySelector('[data-yes]').onclick = save;
  wrap.querySelector('[data-no]').onclick = shut;
  wrap.onclick = e => { if (e.target === wrap) shut(); };
  document.addEventListener('keydown', bye);
  input.focus();
  input.select();
}

async function cmdTags(list) {
  const st0 = await csend('/api/files/tagstates?ids=' + list.join(','), 'GET');

  const wrap = modalBox(`
    <h2>Теги</h2>
    <p>Выделено объектов: ${list.length}. Промежуточная галочка означает,
       что тег стоит не у всех.</p>
    <div class="modal-groups" id="mg"></div>
    <div class="modal-acts"><button class="btn btn-primary" data-no>Готово</button></div>`);

  const shut = () => { wrap.remove(); document.removeEventListener('keydown', bye); };
  const bye = e => { if (e.key === 'Escape') shut(); };

  const draw = st => {
    wrap.querySelector('#mg').innerHTML = st.groups.map(g => `
      <div class="group">
        <h3 style="cursor:default">${esc(g.name)}${g.isMulti ? '' : ' <span class="single">один тег</span>'}</h3>
        ${g.tags.map(t => `
          <button class="tag" data-tag="${t.id}" data-state="${t.state}">
            <span class="box ${t.state}${g.isMulti ? '' : ' radio'}">
              <svg class="ic"><use href="#${t.state === 'partial' ? 'i-minus' : 'i-check'}"></use></svg>
            </span>
            <span class="lbl">${esc(t.name)}</span>
          </button>`).join('')}
      </div>`).join('');

    wrap.querySelectorAll('[data-tag]').forEach(el => {
      el.onclick = async () => {
        // Промежуточное состояние: первый клик ставит тег всем выделенным.
        await csend('/api/tag', 'POST',
          { fileIds: list, tagId: +el.dataset.tag, on: el.dataset.state !== 'on' });
        draw(await csend('/api/files/tagstates?ids=' + list.join(','), 'GET'));
        cmd().reload?.();
      };
    });
  };

  draw(st0);
  wrap.querySelector('[data-no]').onclick = shut;
  wrap.onclick = e => { if (e.target === wrap) shut(); };
  document.addEventListener('keydown', bye);
}

async function cmdDelete(list, one) {
  const ok = await askBox({
    title: one
      ? `Удалить «${esc(one.name)}»?`
      : `Удалить ${list.length} ${plural(list.length, 'файл', 'файла', 'файлов')}?`,
    text: 'Уйдёт в корзину Windows, насовсем не стирается. Достанешь обратно, ' +
          'и файл вернётся вместе со всеми тегами.',
  });
  if (!ok) return;

  const r = await csend('/api/files/delete', 'POST', { ids: list });
  await cmd().reload?.();
  toast(r.errors?.length ? 'Ошибки: ' + r.errors.join('; ')
                         : `Удалено в корзину: ${r.deleted}`,
        r.errors?.length ? 'err' : '');
}

if (cbar) {
  cbar.querySelectorAll('[data-act]').forEach(b => {
    b.onclick = () => cmdAct(b.dataset.act);
  });
}

// ── Панель сведений ────────────────────────────────────────────────────

const INFO_KEY = 'tanba-info';
let infoOn = localStorage.getItem(INFO_KEY) === '1';

function setInfo(on) {
  infoOn = on;
  localStorage.setItem(INFO_KEY, on ? '1' : '0');
  cmain?.classList.toggle('with-info', on);
  document.getElementById('infoBtn')?.classList.toggle('on', on);
  if (on) drawInfo();
}

/// Дата целиком, а не как на карточке: там она сокращена до места, а здесь
/// места хватает и человек пришёл сюда именно за подробностями.
///
/// Дата здесь одна, и это дата изменения. Времени создания в панели нет
/// намеренно: в Windows оно означает, когда появилась вот эта копия, а не
/// когда файл сделали. На хранилище это проверено, у всех 104 файлов приёма
/// время создания позже времени изменения, потому что копировали их сюда
/// годы спустя. «Добавлен» ушёл по той же причине: это факт про программу.
function cmdDate(sec) {
  if (!sec) return '';
  return new Date(sec * 1000).toLocaleDateString('ru-RU',
    { day: '2-digit', month: '2-digit', year: 'numeric' })
    + ', ' + new Date(sec * 1000).toLocaleTimeString('ru-RU',
    { hour: '2-digit', minute: '2-digit' });
}

const row = (k, v) => v ? `<div class="irow"><span class="ik">${k}</span><span class="iv">${v}</span></div>` : '';

function drawInfo() {
  if (!cinfo || !infoOn) return;
  const s = cmd();
  const list = (s.selected && s.selected()) || [];

  if (!list.length) {
    cinfo.innerHTML = `<div class="inone">Ничего не выделено.<br>Выбери файл, и здесь будет всё, что о нём известно.</div>`;
    return;
  }

  if (list.length > 1) {
    const rows = list.map(id => s.byId?.(id)).filter(Boolean);
    const bytes = rows.reduce((n, f) => n + (f.size || 0), 0);
    cinfo.innerHTML = `
      <div class="ihead">
        <div class="iname">${num2(list.length)} ${plural(list.length, 'объект', 'объекта', 'объектов')}</div>
      </div>
      ${row('Размер', bytes ? fmtSize(bytes) : '')}
      ${row('Форматы', [...new Set(rows.map(f => f.ext).filter(Boolean))].join(', '))}`;
    return;
  }

  const f = s.byId?.(list[0]);
  if (!f) { cinfo.innerHTML = ''; return; }

  const isCat = f.kind === 'catalog';
  const tags = (f.tags || []).map(id => s.tagName?.(id)).filter(Boolean);

  cinfo.innerHTML = `
    <div class="ipic">
      ${isCat
        ? `<svg class="ic ibig"><use href="#i-folder"></use></svg>`
        : `${ftypeIcon(f.ext)}<img src="/api/thumb/${f.id}?size=256" alt=""
             onload="this.classList.add('ok')" onerror="this.remove()">`}
    </div>
    <div class="ihead"><div class="iname">${esc(f.name)}</div></div>
    ${row('Тип', isCat ? 'Каталог' : (f.ext ? f.ext.toUpperCase() : 'без расширения'))}
    ${row(isCat ? 'Внутри' : 'Размер',
          isCat ? (f.count || 0) + ' ' + plural(f.count || 0, 'объект', 'объекта', 'объектов')
                : (f.size ? fmtSize(f.size) : ''))}
    ${row('Изменён', cmdDate(f.modifiedAt))}
    ${f.movedTo ? `<div class="iwarn">Уехал из хранилища: ${esc(f.movedTo)}</div>` : ''}
    ${tags.length
      ? `<div class=" itags-h">Теги</div>
         <div class="itags">${tags.map(t => `<span class="itag">${esc(t)}</span>`).join('')}</div>`
      : `<div class="itags-h dim">Тегов нет</div>`}`;
}

/// Разделитель разрядов. Своего num на экране разбора нет, а тысячи файлов
/// без него читаются плохо.
const num2 = n => (n || 0).toLocaleString('ru-RU');

document.getElementById('infoBtn')?.addEventListener('click', () => setInfo(!infoOn));

// ── Обновление ─────────────────────────────────────────────────────────

/// Экран зовёт это после каждой перерисовки и смены выделения.
/// Кнопки гаснут, но остаются на месте: рука привыкает тянуться туда, где
/// они были в прошлый раз, и переезжающая панель эту привычку ломает.
window.tanbaCmdSync = function () {
  if (!cbar) return;
  const s = cmd();
  const n = ((s.selected && s.selected()) || []).length;
  cbar.querySelectorAll('[data-act]').forEach(b => {
    // Переименование берёт ровно один объект: имя у каждого своё, и
    // переименовывать пачку одним полем нечестно.
    b.disabled = b.dataset.act === 'rename' ? n !== 1 : n === 0;
  });
  drawInfo();
};

setInfo(infoOn);
window.tanbaCmdSync();
