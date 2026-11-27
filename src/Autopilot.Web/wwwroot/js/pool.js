import { get, post, del } from './api.js';
import { h, clear, put, toast, badge, field, emptyState, busy } from './dom.js';
import * as unattended from './poolunattended.js';

const base = key => `/projects/${encodeURIComponent(key)}`;
const POLL_MS = 5000;

export async function render({ view, key }) {
  const holder = h('div');
  const queue = h('section', { class: 'card' });
  view.append(holder);
  let extra = null;
  let timer = null;
  let stopped = false;
  let items = [];

  function queueCard() {
    clear(queue);
    put(queue,
      h('div', { class: 'row between' }, h('h2', {}, 'Queue'),
        h('button', { type: 'button', class: 'sm', onclick: ev => busy(ev.currentTarget, () => refresh(true)) }, 'Refresh')),
      !items.length ? emptyState('The pool is the queue the agent works through. Add triaged tasks to it from the Tasks page.', { label: 'Pick tasks', href: `#/p/${encodeURIComponent(key)}/tasks` }, 'The pool is empty') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
        h('thead', {}, h('tr', {}, ['#', 'Task', 'Triage', ''].map(t => h('th', {}, t)))),
        h('tbody', {}, items.map((p, index) => h('tr', {},
          h('td', { 'data-label': 'Order' }, String(p.position)),
          h('td', { 'data-label': 'Task' }, `#${p.id} ${p.title}`),
          h('td', { 'data-label': 'Triage' }, p.triage ? [badge(p.triage, p.triage === 'agent-ready' ? 'ok' : 'warn'), p.stale ? badge('stale', 'warn') : null] : badge('not triaged')),
          h('td', { class: 'actions' }, h('div', { class: 'row' },
            h('button', { type: 'button', 'aria-label': 'Move up', disabled: index === 0, onclick: () => move(key, p, items[index - 1].position, refresh) }, '↑'),
            h('button', { type: 'button', 'aria-label': 'Move down', disabled: index === items.length - 1, onclick: () => move(key, p, items[index + 1].position, refresh) }, '↓'),
            h('button', { type: 'button', class: 'danger', onclick: async () => { try { await del(`${base(key)}/pool/${encodeURIComponent(p.id)}`); await refresh(); } catch (e) { toast(e.message, true); } } }, 'Remove')))))))));
  }

  /** Reads the real queue and the live parts of the page again. The settings fields are left alone. */
  async function refresh(loud = false) {
    try {
      const [next] = await Promise.all([get(`${base(key)}/pool${loud ? '?fresh=true' : ''}`), extra?.refreshLive()]);
      items = next;
      queueCard();
    } catch (e) { if (loud) toast(e.message, true); }
  }

  async function load() {
    items = await get(`${base(key)}/pool`);
    extra = await unattended.render({ key, reload: load });
    clear(holder);
    const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', style: 'width:6rem' });
    const number = input => input.value ? Number(input.value) : null;

    queueCard();
    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Pool'),
        h('p', {}, 'Tasks the agent works through, in this order. Every result is a draft that a person merges.'))),
      queue,
      h('section', { class: 'card stack' },
        h('h2', {}, 'Run now'),
        h('p', { class: 'muted' }, 'Starts the pool in the background with the settings below. Only one run can be active in a project at a time.'),
        h('div', { class: 'row' }, field('Most items to try', limit),
          h('button', { class: 'primary', type: 'button', disabled: !items.length, onclick: async ev => {
            await busy(ev.currentTarget, async () => {
              try { await post(`${base(key)}/pool/run`, { limit: number(limit) }); toast('Pool run started. Follow it on the Runs page.'); await load(); }
              catch (e) { toast(e.message, true); }
            });
          } }, 'Run now'))),
      extra.element);
  }

  await load();

  // The queue and the "right now" table follow what happens elsewhere (a run, the command line) while the page is open.
  const tick = () => {
    if (stopped) return;
    if (!document.hidden) refresh().finally(() => { if (!stopped) timer = setTimeout(tick, POLL_MS); });
    else timer = setTimeout(tick, POLL_MS);
  };
  timer = setTimeout(tick, POLL_MS);
  return () => { stopped = true; clearTimeout(timer); };
}

async function move(key, item, position, reload) {
  try { await post(`${base(key)}/pool/${encodeURIComponent(item.id)}/move`, { position }); await reload(); }
  catch (e) { toast(e.message, true); }
}
