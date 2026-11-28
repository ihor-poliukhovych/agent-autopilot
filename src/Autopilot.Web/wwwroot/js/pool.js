import { get, post, del } from './api.js';
import { h, clear, put, toast, badge, field, emptyState, busy } from './dom.js';
import * as unattended from './poolunattended.js';

const base = key => `/projects/${encodeURIComponent(key)}`;
const POLL_MS = 5000;

export const STAGES = ['Planned', 'Queued', 'Running', 'Reviewing', 'Ready for a person', 'Done'];
// The API sends the status as an enum name; the stage label is the same word, spaced out.
const STAGE_KEYS = { Planned: 'Planned', Queued: 'Queued', Running: 'Running', Reviewing: 'Reviewing', ReadyForPerson: 'Ready for a person', NeedsPerson: 'Ready for a person', Done: 'Done' };

/** Statuses outside the six stages (Failed, Cancelled) mark no stage. */
export function stageLabel(status) {
  return STAGE_KEYS[status] ?? null;
}

/** Six stages in a row; the current one is marked and its sub-status shows as small dots under it. */
export function stepper(status) {
  const current = stageLabel(status?.status);
  const sub = status?.subStatus && status.subStatus !== 'None' ? status.subStatus : null;
  return h('ol', { class: 'stepper', 'aria-label': 'Pool run status' }, STAGES.map(stage => {
    const isCurrent = stage === current;
    return h('li', { class: 'step' + (isCurrent ? ' current' : ''), 'aria-current': isCurrent ? 'step' : null },
      h('span', { class: 'step-name' }, stage),
      isCurrent && sub ? h('span', { class: 'sub-dots', title: sub }, h('span', { class: 'dot' }), h('span', { class: 'dot' }), h('span', { class: 'dot' }), h('span', { class: 'sub-name' }, sub)) : null);
  }));
}

export function progressStrip(progress) {
  const p = progress ?? { done: 0, total: 0, parked: 0 };
  const pct = p.total > 0 ? Math.min(100, Math.round(100 * p.done / p.total)) : 0;
  return h('div', { class: 'progress-strip' },
    h('div', { class: 'bar-track' }, h('div', { class: 'bar-fill', style: `width:${pct}%` })),
    h('span', {}, `${p.done} of ${p.total} done`),
    h('span', { class: 'muted' }, `${p.parked} parked`));
}

export function timelineLog(timeline) {
  if (!timeline?.length) return h('p', { class: 'muted' }, 'No events yet.');
  return h('ol', { class: 'event-log' }, timeline.map(e => h('li', {},
    h('time', { class: 'muted' }, new Date(e.time).toLocaleTimeString()),
    h('span', { class: 'event' }, e.event),
    h('span', { class: 'muted' }, e.detail ?? ''))));
}

export async function render({ view, key }) {
  const holder = h('div');
  const queue = h('section', { class: 'card' });
  const statusCard = h('section', { class: 'card stack' });
  view.append(holder);
  let extra = null;
  let timer = null;
  let stopped = false;
  let items = [];
  let status = null;

  function statusView() {
    clear(statusCard);
    put(statusCard, h('h2', {}, 'Status'), stepper(status), progressStrip(status?.progress), h('h3', {}, 'Events'), timelineLog(status?.timeline));
  }

  const readiness = id => (status?.queue ?? []).find(q => q.id === id)?.readiness ?? '';

  function queueCard() {
    clear(queue);
    put(queue,
      h('div', { class: 'row between' }, h('h2', {}, 'Queue'),
        h('button', { type: 'button', class: 'sm', onclick: ev => busy(ev.currentTarget, () => refresh(true)) }, 'Refresh')),
      !items.length ? emptyState('The pool is the queue the agent works through. Add triaged tasks to it from the Tasks page.', { label: 'Pick tasks', href: `#/p/${encodeURIComponent(key)}/tasks` }, 'The pool is empty') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
        h('thead', {}, h('tr', {}, ['#', 'Task', 'Readiness', 'Triage', ''].map(t => h('th', {}, t)))),
        h('tbody', {}, items.map((p, index) => h('tr', {},
          h('td', { 'data-label': 'Order' }, String(p.position)),
          h('td', { 'data-label': 'Task' }, `#${p.id} ${p.title}`),
          h('td', { 'data-label': 'Readiness', class: 'readiness' }, readiness(p.id) || '–'),
          h('td', { 'data-label': 'Triage' }, p.triage ? [badge(p.triage, p.triage === 'agent-ready' ? 'ok' : 'warn'), p.stale ? badge('stale', 'warn') : null] : badge('not triaged')),
          h('td', { class: 'actions' }, h('div', { class: 'row icon-group' },
            h('button', { type: 'button', class: 'row-btn', title: 'Move up', 'aria-label': 'Move up', disabled: index === 0, onclick: () => move(key, p, items[index - 1].position, refresh) }, '↑'),
            h('button', { type: 'button', class: 'row-btn', title: 'Move down', 'aria-label': 'Move down', disabled: index === items.length - 1, onclick: () => move(key, p, items[index + 1].position, refresh) }, '↓'),
            h('button', { type: 'button', class: 'row-btn danger', title: 'Remove', 'aria-label': 'Remove', onclick: async () => { try { await del(`${base(key)}/pool/${encodeURIComponent(p.id)}`); await refresh(); } catch (e) { toast(e.message, true); } } }, '✕')))))))));
  }

  /** Reads the real queue and the live parts of the page again. The settings fields are left alone. */
  async function refresh(loud = false) {
    try {
      const [next, nextStatus] = await Promise.all([get(`${base(key)}/pool${loud ? '?fresh=true' : ''}`), get(`${base(key)}/pool/status`), extra?.refreshLive()]);
      items = next;
      status = nextStatus;
      statusView();
      queueCard();
    } catch (e) { if (loud) toast(e.message, true); }
  }

  async function load() {
    [items, status] = await Promise.all([get(`${base(key)}/pool`), get(`${base(key)}/pool/status`)]);
    extra = await unattended.render({ key, reload: load });
    clear(holder);
    const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', style: 'width:6rem' });
    const number = input => input.value ? Number(input.value) : null;

    queueCard();
    statusView();
    const runNow = h('section', { class: 'card stack' },
        h('h2', {}, 'Run now'),
        h('p', { class: 'muted' }, 'Starts the pool in the background with the settings below. Only one run can be active in a project at a time.'),
        h('div', { class: 'row' }, field('Most items to try', limit),
          h('button', { class: 'primary', type: 'button', disabled: !items.length, onclick: async ev => {
            await busy(ev.currentTarget, async () => {
              try { await post(`${base(key)}/pool/run`, { limit: number(limit) }); toast('Pool run started. Follow it on the Runs page.'); await load(); }
              catch (e) { toast(e.message, true); }
            });
          } }, 'Run now')));
    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Pool'),
        h('p', {}, 'Tasks the agent works through, in this order. Every result is a draft that a person merges.'))),
      h('div', { class: 'app-layout pool-layout' },
        h('div', { class: 'app-left pool-settings stack' }, extra.element, runNow),
        h('div', { class: 'app-right open stack' }, statusCard, queue, extra.right)));
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
