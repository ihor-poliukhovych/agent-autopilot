import { get, post, put } from './api.js';
import { h, clear, toast, badge, ago, field, emptyState, busy, ask } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

export async function render(ctx) {
  if (ctx.rest[0]) return renderDetail(ctx, ctx.rest[0]);
  return renderList(ctx);
}

function triageBadges(item) {
  if (!item.triage) return [badge('not triaged')];
  const t = item.triage;
  return [
    badge(t.label, t.label === 'agent-ready' ? 'ok' : 'warn'),
    badge(`${t.risk} risk`, t.risk === 'high' ? 'bad' : ''),
    t.stale ? badge('stale: text changed since triage', 'warn') : null,
  ];
}

function lastRun(item) {
  const r = item.lastRun;
  if (!r) return h('span', { class: 'muted' }, 'never');
  return h('a', { href: `#/p/${encodeURIComponent(r.projectKey)}/runs/${r.id}` }, `${r.outcome}${r.gateVerdict ? ', gate ' + r.gateVerdict : ''}`);
}

async function renderList(ctx) {
  const { view, key } = ctx;
  const holder = h('div');
  view.append(holder);
  let timer = null;

  async function load() {
    const data = await get(`${base(key)}/tasks`);
    clear(holder);
    holder.append(
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', {}, 'Tasks'), h('p', {}, 'Open work items with their triage, last run and pool state.')),
        h('div', { class: 'row' },
          h('button', { type: 'button', onclick: () => load().catch(e => toast(e.message, true)) }, 'Refresh'),
          data.localTasks ? h('button', { class: 'primary', type: 'button', onclick: () => showAddForm(ctx, holder, load) }, 'Add a task') : null)));
    if (data.legacyTaskFiles) {
      holder.append(h('div', { class: 'banner' }, 'This project still has task files in its folder. Run ', h('code', {}, 'autopilot task import-files'), ' to move them into the workspace so you can edit them here.'));
    }
    if (data.status.busy) {
      holder.append(h('div', { class: 'banner info' }, `A run is active (${data.status.owner}). `, h('a', { href: `#/p/${encodeURIComponent(key)}/runs` }, 'Follow it on the Runs page.')));
    }
    if (!data.items.length) {
      holder.append(h('div', { class: 'card' }, emptyState(data.localTasks ? 'No open tasks. Add the first one.' : 'No open work items were found.')));
      return data;
    }
    const rows = data.items.map(item => {
      const running = data.activeJobs[item.id];
      return h('tr', {},
        h('td', { 'data-label': 'Id' }, item.url ? h('a', { href: item.url, target: '_blank', rel: 'noopener noreferrer' }, item.id) : h('a', { href: `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(item.id)}` }, '#' + item.id)),
        h('td', { 'data-label': 'Task' },
          item.url ? item.title : h('a', { href: `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(item.id)}` }, item.title),
          h('div', { class: 'row', style: 'margin-top:.25rem' }, triageBadges(item)),
          item.triage ? h('div', { class: 'muted small' }, item.triage.reason) : null),
        h('td', { 'data-label': 'Last run' }, lastRun(item)),
        h('td', { 'data-label': 'Pool' }, item.inPool ? badge(`pool #${item.poolPosition}`, 'info') : h('span', { class: 'muted' }, '-')),
        h('td', { class: 'actions' }, h('div', { class: 'row' },
          running ? badge('running', 'info') : h('button', { type: 'button', disabled: data.status.busy, onclick: ev => start(ev.currentTarget, ctx, item, load) }, 'Start'),
          item.inPool ? null : h('button', { type: 'button', onclick: ev => toPool(ev.currentTarget, ctx, item, load) }, 'Add to pool'),
          item.triage && !item.triage.stale ? null : h('button', { type: 'button', class: 'ghost', title: 'Ask the agent to triage this item', onclick: ev => triage(ev.currentTarget, ctx, item) }, 'Triage'))));
    });
    holder.append(h('div', { class: 'card table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Id', 'Task', 'Last run', 'Pool', ''].map(t => h('th', {}, t)))),
      h('tbody', {}, rows))));
    return data;
  }

  const data = await load();
  if (data.status.busy) timer = setInterval(() => load().catch(() => {}), 5000);
  return () => { if (timer) clearInterval(timer); };
}

async function start(button, { key }, item, reload) {
  const ok = await ask({
    title: `Start task ${item.id}?`,
    body: [h('p', {}, item.title), h('p', { class: 'muted' }, 'The agent works in a separate checkout and ends with a draft. Nothing is merged: a person decides.')],
    confirm: 'Start',
  });
  if (!ok) return;
  await busy(button, async () => {
    try { await post(`${base(key)}/tasks/${encodeURIComponent(item.id)}/start`); toast('Started. Follow it on the Runs page.'); await reload(); }
    catch (e) { toast(e.message, true); }
  });
}

async function toPool(button, { key }, item, reload) {
  await busy(button, async () => {
    try { await post(`${base(key)}/pool`, { id: item.id }); toast('Added to the pool.'); await reload(); }
    catch (e) { toast(e.message, true); }
  });
}

async function triage(button, { key }, item) {
  await busy(button, async () => {
    try { await post(`${base(key)}/tasks/${encodeURIComponent(item.id)}/triage`); toast('Triage started in the background. Refresh in a minute.'); }
    catch (e) { toast(e.message, true); }
  });
}

function showAddForm(ctx, holder, reload) {
  const title = h('input', { type: 'text', maxlength: 300, required: true });
  const body = h('textarea', { rows: 6, placeholder: 'What should change, and how will you know it works?' });
  const labels = h('input', { type: 'text', placeholder: 'optional, comma separated' });
  const form = h('form', { class: 'card stack' },
    h('h2', {}, 'New task'),
    field('Title', title),
    field('Description', body, 'The agent reads this text. Say what done looks like: it makes triage and the gate far more reliable.'),
    field('Labels', labels),
    h('div', { class: 'row' },
      h('button', { class: 'primary', type: 'submit' }, 'Add task'),
      h('button', { type: 'button', onclick: () => form.remove() }, 'Cancel')));
  form.addEventListener('submit', async ev => {
    ev.preventDefault();
    try {
      await post(`${base(ctx.key)}/tasks`, { title: title.value, body: body.value, labels: labels.value.split(',').map(s => s.trim()).filter(Boolean) });
      toast('Task added.');
      await reload();
    } catch (e) { toast(e.message, true); }
  });
  holder.children[0].after(form);
  title.focus();
}

async function renderDetail(ctx, id) {
  const { view, key } = ctx;
  const holder = h('div');
  view.append(holder);
  const number = Number(id);

  async function load() {
    let detail;
    try { detail = await get(`${base(key)}/tasks/${number}`); }
    catch (e) { clear(holder); holder.append(h('div', { class: 'banner bad' }, e.message), h('a', { href: `#/p/${encodeURIComponent(key)}/tasks` }, 'Back to the tasks')); return; }
    const t = detail.task;
    const title = h('input', { type: 'text', value: t.title, maxlength: 300 });
    const body = h('textarea', { rows: 8 }); body.value = t.body;
    const labels = h('input', { type: 'text', value: t.labels.join(', ') });
    const comment = h('textarea', { rows: 3, placeholder: 'Write a comment' });
    clear(holder);
    holder.append(
      h('p', {}, h('a', { href: `#/p/${encodeURIComponent(key)}/tasks` }, '← All tasks')),
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', {}, `#${t.number} ${t.title}`),
          h('p', {}, badge(t.state, t.closed ? '' : 'ok'), ' ', `added by ${t.author} ${ago(t.createdAt)}`)),
        h('div', { class: 'row' },
          t.closed ? null : h('button', { type: 'button', onclick: async ev => { await busy(ev.currentTarget, async () => { try { await post(`${base(key)}/tasks/${encodeURIComponent(t.number)}/start`); toast('Started.'); } catch (e) { toast(e.message, true); } }); } }, 'Start'),
          t.closed ? null : h('button', { type: 'button', onclick: async () => { try { await post(`${base(key)}/pool`, { id: t.id }); toast('Added to the pool.'); } catch (e) { toast(e.message, true); } } }, 'Add to pool'),
          h('button', { type: 'button', class: t.closed ? '' : 'danger', onclick: async () => {
            try { await post(`${base(key)}/tasks/${t.number}/${t.closed ? 'reopen' : 'close'}`); await load(); } catch (e) { toast(e.message, true); }
          } }, t.closed ? 'Reopen' : 'Close task'))),
      h('form', { class: 'card stack', onsubmit: async ev => {
        ev.preventDefault();
        try { await put(`${base(key)}/tasks/${t.number}`, { title: title.value, body: body.value, labels: labels.value.split(',').map(s => s.trim()).filter(Boolean) }); toast('Saved. Triage is now stale: triage again.'); await load(); }
        catch (e) { toast(e.message, true); }
      } },
        h('h2', {}, 'Text'), field('Title', title), field('Description', body), field('Labels', labels),
        h('div', {}, h('button', { class: 'primary', type: 'submit' }, 'Save changes'))),
      h('section', { class: 'card' }, h('h2', {}, 'Comments'),
        detail.comments.length ? detail.comments.map(c => h('div', { class: 'comment' }, h('div', { class: 'who' }, `${c.author} · ${ago(c.createdAt)}`), h('div', { class: 'body' }, c.body))) : h('p', { class: 'muted' }, 'No comments yet.'),
        h('div', { class: 'stack' }, comment, h('button', { type: 'button', onclick: async () => {
          try { await post(`${base(key)}/tasks/${t.number}/comments`, { text: comment.value }); await load(); } catch (e) { toast(e.message, true); }
        } }, 'Add comment'))),
      h('section', { class: 'card' }, h('h2', {}, 'History'),
        detail.history.length ? h('ul', { class: 'stack', style: 'padding-left:1.1rem;margin:0' }, detail.history.map(e => h('li', {}, `${e.summary} `, h('span', { class: 'muted small' }, ago(e.createdAt)), e.detail ? h('div', { class: 'muted small prewrap' }, e.detail) : null))) : h('p', { class: 'muted' }, 'Nothing yet.')));
  }
  await load();
}
