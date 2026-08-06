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

function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
}

// ── Отрисовка ──────────────────────────────────────────────────────────

async function load() {
  tree = await jget('/api/tagtree');
  render();
}

function render() {
  const groups = tree.groups.map(g => `
    <div class="gcard" data-group="${g.id}">
      <input class="gname" value="${esc(g.name)}" spellcheck="false">
      <div class="gflags">
        <button class="flag${g.isMulti ? ' on' : ''}" data-flag="multi"
                title="Можно ли повесить на файл несколько тегов этой группы">несколько тегов</button>
        <button class="flag${g.isRequired ? ' on' : ''}" data-flag="required"
                title="Файл без тега из этой группы считается неразмеченным">обязательная</button>
      </div>

      ${g.tags.map(t => `
        <div class="trow" data-tag="${t.id}">
          <input class="tname" value="${esc(t.name)}" spellcheck="false">
          <span class="tcount">${t.count || ''}</span>
          <div class="tacts">
            <button data-act="merge" title="Объединить с другим тегом">
              <svg class="ic"><use href="#i-merge"></use></svg></button>
            <button data-act="del" class="del" title="Удалить тег">
              <svg class="ic"><use href="#i-trash"></use></svg></button>
          </div>
        </div>`).join('')}

      <div class="tnew"><input placeholder="новый тег…" data-newtag="${g.id}"></div>

      <div class="gdel">
        <button class="mini mini-ghost" data-delgroup="${g.id}">Удалить группу</button>
      </div>
    </div>`).join('');

  $('editor').innerHTML = groups + `
    <div class="gcard new">
      <input class="gname" placeholder="новая группа…" id="newGroup" spellcheck="false">
      <p class="dim" style="margin:6px 0 0">Enter создаёт группу</p>
    </div>`;

  wire();

  const nt = tree.groups.reduce((s, g) => s + g.tags.length, 0);
  $('stCount').textContent =
    `${tree.groups.length} ${plural(tree.groups.length, 'группа', 'группы', 'групп')}, ` +
    `${nt} ${plural(nt, 'тег', 'тега', 'тегов')}`;
}

function wire() {
  // Имя группы. Правится на месте, сохраняется по уходу фокуса или Enter.
  $('editor').querySelectorAll('.gcard[data-group]').forEach(card => {
    const id = +card.dataset.group;
    const input = card.querySelector('.gname');
    const was = input.value;

    const save = async () => {
      const nm = input.value.trim();
      if (!nm || nm === was) { input.value = was; return; }
      try { await send('PATCH', '/api/groups/' + id, { name: nm }); await load(); toast('Переименовано'); }
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
            title: `Тег «${nm}» уже есть`,
            text: 'Объединить их? Все файлы, помеченные этим тегом, получат существующий, а лишний исчезнет.',
            ok: 'Объединить', danger: false,
          });
          if (ok) {
            const m = await send('POST', `/api/tags/${id}/merge`, { intoId: r.into });
            toast(`Объединено, перенесено расстановок: ${m.moved}`);
          }
        } else toast('Переименовано');
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

  $('newGroup').onkeydown = async e => {
    if (e.key !== 'Enter' || !$('newGroup').value.trim()) return;
    await send('POST', '/api/groups', { name: $('newGroup').value.trim(), isMulti: true });
    await load();
    toast('Группа создана');
  };
}

// ── Удаление и объединение ─────────────────────────────────────────────

async function delTag(id, name) {
  const r = await send('DELETE', '/api/tags/' + id);
  if (r.needConfirm) {
    const ok = await askBox({
      title: `Удалить тег «${name}»?`,
      text: `Он стоит на ${r.marks} ${plural(r.marks, 'файле', 'файлах', 'файлах')}. ` +
            `Файлы останутся на диске и в библиотеке, пропадёт только эта пометка.`,
    });
    if (!ok) return;
    await send('DELETE', `/api/tags/${id}?force=true`);
  }
  await load();
  toast('Тег удалён');
}

async function delGroup(id) {
  const g = tree.groups.find(x => x.id === id);
  const r = await send('DELETE', '/api/groups/' + id);
  if (r.needConfirm) {
    const ok = await askBox({
      title: `Удалить группу «${g ? g.name : ''}» целиком?`,
      text: `В ней ${r.tags} ${plural(r.tags, 'тег', 'тега', 'тегов')}, ` +
            `расставленных ${r.marks} ${plural(r.marks, 'раз', 'раза', 'раз')}. ` +
            `Файлы останутся на месте, пропадут только пометки.`,
      ok: 'Удалить группу',
    });
    if (!ok) return;
    await send('DELETE', `/api/groups/${id}?force=true`);
  }
  await load();
  toast('Группа удалена');
}

// Объединение: главный инструмент уборки, когда завелись
// «Профориентация» и «профориентация».
async function mergeTag(id, name) {
  const all = tree.groups.flatMap(g => g.tags.map(t => ({ ...t, group: g.name })))
                         .filter(t => t.id !== id);
  if (!all.length) return toast('Объединять не с чем');

  const into = await pickBox({
    title: `Объединить «${name}» с другим тегом`,
    text: 'Файлы этого тега получат выбранный, а сам он исчезнет.',
    items: all,
    label: t => esc(t.name),
    hint: t => `${esc(t.group)}${t.count ? ', ' + t.count : ''}`,
  });
  if (!into) return;

  const r = await send('POST', `/api/tags/${id}/merge`, { intoId: into.id });
  await load();
  toast(`Объединено с «${into.name}», перенесено: ${r.moved}`);
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
