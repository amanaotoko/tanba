'use strict';

// Редактор структуры тегов: группы и теги, переименование, перенос,
// объединение и удаление. Расстановкой занимаются другие экраны.

let tree = { groups: [] };

const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const send = async (method, url, body) => {
  const r = await fetch(url, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
};
const jget = url => send('GET', url);

// ── Отрисовка ──────────────────────────────────────────────────────────

async function load() {
  tree = await jget('/api/tagtree');
  render();
}

// Поиск по имени тега. Групп до десятка, а тегов в одной группе бывает
// несколько десятков, и глазами их уже не пробежать.
let find = '';

function render() {
  // При поиске показываем только совпавшие теги и только те группы,
  // где что-то нашлось: пустые карточки групп сбивают счёт находкам.
  const shown = find
    ? tree.groups
        .map(g => ({ ...g, tags: g.tags.filter(tag => tag.name.toLowerCase().includes(find)) }))
        .filter(g => g.tags.length)
    : tree.groups;

  // Порядок тегов внутри группы наш, не базы: база сравнивает имена байтами
  // и уводит всю латиницу вперёд кириллицы, а в списке они должны идти
  // вперемешку. Сами имена при этом не трогаем, это данные человека.
  const cards = shown.map(g => ({
    ...g, tags: g.tags.slice().sort((a, b) => I18N.byName(a.name, b.name)),
  }));

  const groups = cards.map(g => `
    <div class="gcard" data-group="${g.id}">
      <input class="gname" value="${esc(g.name)}" spellcheck="false">
      <div class="gflags">
        <button class="flag${g.isMulti ? ' on' : ''}" data-flag="multi"
                title="${t('tags.flag.multi.tip')}">${t('tags.flag.multi')}</button>
        <button class="flag${g.isRequired ? ' on' : ''}" data-flag="required"
                title="${t('tags.flag.required.tip')}">${t('tags.flag.required')}</button>
      </div>

      ${g.tags.map(tag => `
        <div class="trow" data-tag="${tag.id}">
          <input class="tname" value="${esc(tag.name)}" spellcheck="false">
          <span class="tcount">${tag.count ? I18N.num(tag.count) : ''}</span>
          <div class="tacts">
            <button data-act="merge" title="${t('tags.merge.tip')}">
              <svg class="ic"><use href="#i-merge"></use></svg></button>
            <button data-act="del" class="del" title="${t('tags.del.tip')}">
              <svg class="ic"><use href="#i-trash"></use></svg></button>
          </div>
        </div>`).join('')}

      <div class="tnew"><input placeholder="${t('common.tag.new')}" data-newtag="${g.id}"></div>

      <div class="gdel">
        <button class="mini mini-ghost" data-delgroup="${g.id}">${t('tags.group.del')}</button>
      </div>
    </div>`).join('');

  // Карточку новой группы при поиске не показываем: заводить группу,
  // пока ищешь тег, никто не собирается, а находки она сбивает.
  $('editor').innerHTML = find
    ? (groups || `<p class="dim">${t('common.tagNotFound')}</p>`)
    : groups + `
    <div class="gcard new">
      <input class="gname" placeholder="${t('tags.group.new.placeholder')}" id="newGroup" spellcheck="false">
      <p class="dim" style="margin:6px 0 0">${t('tags.group.new.hint')}</p>
    </div>`;

  wire();

  const nt = tree.groups.reduce((s, g) => s + g.tags.length, 0);
  const found = shown.reduce((s, g) => s + g.tags.length, 0);
  $('stCount').textContent = find
    ? tn(found, 'tags.status.found', { total: I18N.num(nt) })
    : tn(tree.groups.length, 'tags.status.groups') + ', ' + tn(nt, 'tags.status.tags');
}

$('q').oninput = () => { find = $('q').value.trim().toLowerCase(); render(); };

function wire() {
  // Имя группы. Правится на месте, сохраняется по уходу фокуса или Enter.
  $('editor').querySelectorAll('.gcard[data-group]').forEach(card => {
    const id = +card.dataset.group;
    const input = card.querySelector('.gname');
    const was = input.value;

    const save = async () => {
      const nm = input.value.trim();
      if (!nm || nm === was) { input.value = was; return; }
      try { await send('PATCH', '/api/groups/' + id, { name: nm }); await load(); toast(t('toast.renamed')); }
      catch (e) { input.value = was; toast(err(e), 'err'); }
    };
    input.onblur = save;
    input.onkeydown = e => {
      if (e.key === 'Enter') input.blur();
      if (e.key === 'Escape') { input.value = was; input.blur(); }
    };

    card.querySelectorAll('[data-flag]').forEach(b => {
      b.onclick = async () => {
        const on = !b.classList.contains('on');
        const key = b.dataset.flag === 'multi' ? 'isMulti' : 'isRequired';
        await send('PATCH', '/api/groups/' + id, { [key]: on });
        await load();
      };
    });
  });

  // Имя тега. Совпало с тёзкой в той же группе, значит предлагаем объединить,
  // а не молча падаем на ограничении базы.
  $('editor').querySelectorAll('.trow').forEach(row => {
    const id = +row.dataset.tag;
    const input = row.querySelector('.tname');
    const was = input.value;

    const save = async () => {
      const nm = input.value.trim();
      if (!nm || nm === was) { input.value = was; return; }
      try {
        const r = await send('PATCH', '/api/tags/' + id, { name: nm });
        if (r.needMerge) {
          input.value = was;
          const ok = await askBox({
            title: t('tags.mergeExists.title', { name: esc(nm) }),
            text: t('tags.mergeExists.body'),
            ok: t('tags.merge.ok'), danger: false,
          });
          if (ok) {
            const m = await send('POST', `/api/tags/${id}/merge`, { intoId: r.into });
            toast(tn(m.moved, 'toast.mergedMarks'));
          }
        } else toast(t('toast.renamed'));
        await load();
      } catch (e) { input.value = was; toast(err(e), 'err'); }
    };
    input.onblur = save;
    input.onkeydown = e => {
      if (e.key === 'Enter') input.blur();
      if (e.key === 'Escape') { input.value = was; input.blur(); }
    };

    row.querySelector('[data-act="del"]').onclick = () => delTag(id, was);
    row.querySelector('[data-act="merge"]').onclick = () => mergeTag(id, was);
  });

  $('editor').querySelectorAll('[data-newtag]').forEach(input => {
    input.onkeydown = async e => {
      if (e.key !== 'Enter' || !input.value.trim()) return;
      await send('POST', '/api/tags', { groupId: +input.dataset.newtag, name: input.value.trim() });
      input.value = '';
      await load();
    };
  });

  $('editor').querySelectorAll('[data-delgroup]').forEach(b => {
    b.onclick = () => delGroup(+b.dataset.delgroup);
  });

  // При поиске карточки новой группы нет, и это нормально.
  if (!$('newGroup')) return;
  $('newGroup').onkeydown = async e => {
    if (e.key !== 'Enter' || !$('newGroup').value.trim()) return;
    await send('POST', '/api/groups', { name: $('newGroup').value.trim(), isMulti: true });
    await load();
    toast(t('toast.groupCreated'));
  };
}

// ── Удаление и объединение ─────────────────────────────────────────────

async function delTag(id, name) {
  const r = await send('DELETE', '/api/tags/' + id);
  if (r.needConfirm) {
    const ok = await askBox({
      title: t('tags.del.title', { name: esc(name) }),
      text: tn(r.marks, 'tags.del.body'),
    });
    if (!ok) return;
    await send('DELETE', `/api/tags/${id}?force=true`);
  }
  await load();
  toast(t('toast.tagDeleted'));
}

async function delGroup(id) {
  const g = tree.groups.find(x => x.id === id);
  const r = await send('DELETE', '/api/groups/' + id);
  if (r.needConfirm) {
    const ok = await askBox({
      title: t('tags.delGroup.title', { name: esc(g ? g.name : '') }),
      text: tn(r.tags, 'tags.delGroup.body', { marks: tn(r.marks, 'tags.delGroup.marks') }),
      ok: t('tags.group.del'),
    });
    if (!ok) return;
    await send('DELETE', `/api/groups/${id}?force=true`);
  }
  await load();
  toast(t('toast.groupDeleted'));
}

// Объединение: главный инструмент уборки, когда завелись
// «Профориентация» и «профориентация».
async function mergeTag(id, name) {
  const all = tree.groups.flatMap(g => g.tags.map(tag => ({ ...tag, group: g.name })))
                         .filter(tag => tag.id !== id)
                         .sort((a, b) => I18N.byName(a.name, b.name));
  if (!all.length) return toast(t('toast.nothingToMerge'));

  const into = await pickBox({
    title: t('tags.merge.title', { name: esc(name) }),
    text: t('tags.merge.body'),
    items: all,
    label: tag => esc(tag.name),
    hint: tag => `${esc(tag.group)}${tag.count ? ', ' + I18N.num(tag.count) : ''}`,
  });
  if (!into) return;

  const r = await send('POST', `/api/tags/${id}/merge`, { intoId: into.id });
  await load();
  toast(tn(r.moved, 'toast.merged', { name: into.name }));
}

// ── Мелочи ─────────────────────────────────────────────────────────────

const err = e => { try { return JSON.parse(e.message).error || e.message; } catch { return e.message; } };

let toastTimer;
function toast(text, cls = '') {
  const el = $('toast');
  el.textContent = text;
  el.className = 'toast show ' + cls;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.className = 'toast ' + cls, 3000);
}

// Разметка страницы и шапка уже на месте: проставляем в них тексты до того,
// как придёт дерево, чтобы поле поиска не мигало чужим языком.
I18N.apply();
load();
