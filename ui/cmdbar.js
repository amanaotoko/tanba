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
    toast(t('toast.actionFailed', { error: e.message }), 'err');
  }
}

function cmdRename(file) {
  const wrap = modalBox(`
    <h2>${t('common.cmd.rename')}</h2>
    <p>${t('rename.body')}</p>
    <input class="name" value="${esc(file.name.replace(/\.[^.]+$/, ''))}" spellcheck="false">
    <div class="modal-acts">
      <button class="btn" data-no>${t('common.cancel')}</button>
      <button class="btn btn-primary" data-yes>${t('common.cmd.rename')}</button>
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
      toast(t('toast.renamed'));
    } catch (e) { toast(t('toast.actionFailed', { error: e.message }), 'err'); }
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
    <h2>${t('tagsdlg.title')}</h2>
    <p>${t('tagsdlg.body', { sel: tn(list.length, 'tagsdlg.count') })}</p>
    <div class="modal-groups" id="mg"></div>
    <div class="modal-acts"><button class="btn btn-primary" data-no>${t('common.done')}</button></div>`);

  const shut = () => { wrap.remove(); document.removeEventListener('keydown', bye); };
  const bye = e => { if (e.key === 'Escape') shut(); };

  const draw = st => {
    wrap.querySelector('#mg').innerHTML = st.groups.map(g => `
      <div class="group">
        <h3 style="cursor:default">${esc(g.name)}${g.isMulti ? '' : ` <span class="single">${t('tag.group.single')}</span>`}</h3>
        ${g.tags.map(tag => `
          <button class="tag" data-tag="${tag.id}" data-state="${tag.state}">
            <span class="box ${tag.state}${g.isMulti ? '' : ' radio'}">
              <svg class="ic"><use href="#${tag.state === 'partial' ? 'i-minus' : 'i-check'}"></use></svg>
            </span>
            <span class="lbl">${esc(tag.name)}</span>
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
      ? t('del.one.title', { name: esc(one.name) })
      : tn(list.length, 'del.many.title'),
    text: t('del.body'),
  });
  if (!ok) return;

  const r = await csend('/api/files/delete', 'POST', { ids: list });
  await cmd().reload?.();
  toast(r.errors?.length ? t('toast.errors', { list: r.errors.join('; ') })
                         : t('toast.deleted', { n: I18N.num(r.deleted) }),
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
  return I18N.date(sec) + ', ' + I18N.time(sec);
}

const row = (k, v) => v ? `<div class="irow"><span class="ik">${k}</span><span class="iv">${v}</span></div>` : '';

function drawInfo() {
  if (!cinfo || !infoOn) return;
  const s = cmd();
  const list = (s.selected && s.selected()) || [];

  if (!list.length) {
    cinfo.innerHTML = `<div class="inone">${t('info.empty')}<br>${t('info.emptyHint')}</div>`;
    return;
  }

  if (list.length > 1) {
    const rows = list.map(id => s.byId?.(id)).filter(Boolean);
    const bytes = rows.reduce((n, f) => n + (f.size || 0), 0);
    cinfo.innerHTML = `
      <div class="ihead">
        <div class="iname">${tn(list.length, 'info.items')}</div>
      </div>
      ${row(t('info.size'), bytes ? fmtSize(bytes) : '')}
      ${row(t('info.formats'), [...new Set(rows.map(f => f.ext).filter(Boolean))].join(', '))}`;
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
        : `${ftypeIcon(f.ext)}<img src="/api/thumb/${f.id}?size=256${f.modifiedAt ? '&v=' + f.modifiedAt : ''}" alt=""
             onload="this.classList.add('ok')" onerror="this.remove()">`}
    </div>
    <div class="ihead"><div class="iname">${esc(f.name)}</div></div>
    ${row(t('info.type'), isCat ? t('info.type.catalog')
                                : (f.ext ? f.ext.toLocaleUpperCase('en') : t('info.type.noExt')))}
    ${isCat
      ? row(t('info.inside'), tn(f.count || 0, 'info.items'))
      : row(t('info.size'), f.size ? fmtSize(f.size) : '')}
    ${row(t('info.modified'), cmdDate(f.modifiedAt))}
    ${f.movedTo ? `<div class="iwarn">${t('info.movedAway', { path: esc(f.movedTo) })}</div>` : ''}
    ${tags.length
      ? `<div class=" itags-h">${t('info.tags')}</div>
         <div class="itags">${tags.map(tag => `<span class="itag">${esc(tag)}</span>`).join('')}</div>`
      : `<div class="itags-h dim">${t('info.noTags')}</div>`}`;
}

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
