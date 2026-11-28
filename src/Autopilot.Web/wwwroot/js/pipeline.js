import { get, post } from './api.js';
import { h, clear, toast, badge, ago, emptyState, loading, busy, ask, keepStickyFit } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;
const link = (key, path) => `#/p/${encodeURIComponent(key)}/${path}`;

export const STAGES = [
  ['backlog', 'Backlog'], ['triaged', 'Triaged'], ['pool', 'In pool'], ['run', 'Running'],
  ['gate', 'Gate'], ['pr', 'Draft PR'], ['person', 'Needs a person'], ['done', 'Done'],
];

/** Which lane a task is in, from its triage, pool state, last run and active job. */
export function stageOf(item, job) {
  if (job) return /gate|build|test/i.test(job.step || '') ? 'gate' : 'run';
  const r = item.lastRun;
  if (r && r.humanDecision === 'merged') return 'done';
  if (r && !r.humanDecision) {
    if (r.gateVerdict === 'passed') return 'pr';
    if (r.gateVerdict === 'rejected') return 'gate';
  }
  if ((r && r.humanDecision === 'rejected') || (item.triage && item.triage.label !== 'agent-ready')) return 'person';
  if (item.inPool) return 'pool';
  return item.triage ? 'triaged' : 'backlog';
}

export function duration(ms) {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${s % 60 ? (s % 60) + 's' : ''}`.trim();
  const hrs = Math.floor(m / 60);
  return hrs < 48 ? `${hrs} h ${m % 60} min` : `${Math.floor(hrs / 24)} d ${hrs % 24} h`;
}

function median(values) {
  if (!values.length) return null;
  const v = [...values].sort((a, b) => a - b);
  return v[Math.floor(v.length / 2)];
}

function funnel(stats, runs) {
  const tiles = [];
  const merged = runs.filter(r => r.humanDecision === 'merged' && r.humanDecidedAt)
    .map(r => new Date(r.humanDecidedAt) - new Date(r.startedAt)).filter(x => x >= 0);
  const mid = median(merged);
  if (mid !== null) tiles.push(['Time to merge', duration(mid), `median of ${merged.length} merged`]);
  if (stats.totalRuns > 0) tiles.push(['First-pass rate', `${Math.round(100 * stats.gatePassed / stats.totalRuns)}%`, `${stats.gatePassed} of ${stats.totalRuns} runs passed the gate`]);
  if (stats.decided > 0) tiles.push(['Person agrees with gate', `${Math.round(100 * stats.agreementRate)}%`, `${stats.agreed} of ${stats.decided} decisions`]);
  if (!tiles.length) return null;
  return h('section', { class: 'funnel', 'aria-label': 'Funnel' }, tiles.map(([label, value, sub]) =>
    h('div', { class: 'tile' }, h('div', { class: 'label' }, label), h('div', { class: 'stat' }, value), h('div', { class: 'sub' }, sub))));
}

function card(key, item, job, stage) {
  const r = item.lastRun;
  const href = item.url || link(key, `tasks/${encodeURIComponent(item.id)}`);
  const target = job ? link(key, `pipeline/job/${encodeURIComponent(job.id)}`) : (r && stage !== 'backlog' && stage !== 'triaged' && stage !== 'pool' ? link(key, `runs/${r.id}`) : href);
  const chips = [];
  if (item.triage) {
    chips.push(badge(item.triage.label, item.triage.label === 'agent-ready' ? 'ok' : 'warn'));
    chips.push(badge(`${item.triage.risk} risk`, item.triage.risk === 'high' ? 'bad' : ''));
  }
  if (item.inPool) chips.push(badge(`pool #${item.poolPosition}`, 'info'));
  let step = '';
  if (job) step = `${job.step || job.status} · ${duration(Date.now() - new Date(job.startedAt || job.createdAt))}`;
  else if (r) step = `${r.outcome} · ${ago(r.finishedAt)}`;
  return h('a', { class: 'tcard' + (job ? ' is-run' : ''), href: target },
    h('div', { class: 'id' }, `#${item.id}`),
    h('div', { class: 'title' }, item.title),
    h('div', { class: 'meta' }, chips),
    step ? h('div', { class: 'foot' }, h('span', {}, step), job ? h('span', { class: 'spinner', 'aria-hidden': 'true' }) : null) : null);
}

const EMPTY_CARD = { dots: [], outsideAutopilot: false, release: null, actionDue: false, actionLabel: null, labels: [] };
const cardOf = (tasks, item) => (tasks.cards && tasks.cards[item.id]) || EMPTY_CARD;
const labelsOf = (item, c) => item.labels || c.labels || [];

/** Filters only, no grouping: an item stays in the list when it matches every chosen value. */
export function matches(item, c, f) {
  return (!f.state || item.state === f.state)
    && (!f.assignee || item.assignedTo === f.assignee)
    && (!f.release || c.release === f.release)
    && (!f.label || labelsOf(item, c).includes(f.label));
}

function filtersBar(tasks, filters, repaint) {
  const uniq = values => [...new Set(values.filter(Boolean))].sort();
  const cards = tasks.items.map(i => [i, cardOf(tasks, i)]);
  const select = (name, label, values) => {
    const el = h('select', { 'data-filter': name, onchange: ev => { filters[name] = ev.target.value; repaint(); } },
      h('option', { value: '' }, 'All'), values.map(v => h('option', { value: v }, v)));
    if (values.includes(filters[name])) el.value = filters[name]; else filters[name] = '';
    return h('label', { class: 'pf' }, h('span', { class: 'muted small' }, label), el);
  };
  return h('div', { class: 'pipeline-filters', role: 'group', 'aria-label': 'Filters' },
    select('state', 'State', uniq(tasks.items.map(i => i.state))),
    select('assignee', 'Assignee', uniq(tasks.items.map(i => i.assignedTo))),
    select('release', 'Release', uniq(cards.map(([, c]) => c.release))),
    select('label', 'Label', uniq(cards.flatMap(([i, c]) => labelsOf(i, c)))));
}

/** One card: id, outside-autopilot badge and age, the wrapped title, then the dot strip and a button only when a person is due. */
function pipelineCard(key, item, c, isSelected, onSelect) {
  const r = item.lastRun;
  const when = (r && r.finishedAt) || (item.triage && item.triage.triagedAt);
  const target = r && c.actionDue ? link(key, `runs/${r.id}`) : (item.url || link(key, `tasks/${encodeURIComponent(item.id)}`));
  return h('div', { class: 'card pcard' + (isSelected ? ' selected' : ''), 'data-id': item.id, 'aria-current': isSelected ? 'true' : null, onclick: onSelect },
    h('div', { class: 'pcard-head' },
      h('span', { class: 'pcard-id' }, `#${item.id}`),
      c.outsideAutopilot ? h('span', { class: 'badge-dashed' }, 'outside autopilot') : null,
      h('span', { class: 'pcard-age muted small' }, when ? ago(when) : '')),
    h('div', { class: 'pcard-title' }, item.title),
    h('div', { class: 'pcard-strip' },
      h('ol', { class: 'dots', 'aria-label': 'Pipeline steps' }, c.dots.map(d =>
        h('li', { class: `dot ${d.state}`, title: `${d.step}: ${d.state}` }, h('span', { class: 'sr-only' }, `${d.step}: ${d.state}`)))),
      c.actionDue ? h('a', { class: 'btn sm primary', href: target, onclick: ev => ev.stopPropagation() }, c.actionLabel || 'Open') : null));
}


function stepper(stages) {
  return h('ol', { class: 'stepper', 'aria-label': 'Pipeline stages' }, stages.map(s =>
    h('li', { class: `stage ${s.state}`, 'data-stage': s.step },
      h('span', { class: 'node', title: `${s.step}: ${s.state}` }, h('span', { class: 'sr-only' }, `${s.step}: ${s.state}`)),
      h('div', { class: 'stage-name' }, s.step),
      (s.subSteps || []).length ? h('ul', { class: 'substeps' }, s.subSteps.map(x =>
        h('li', { class: `sub ${x.state || 'pending'}` }, h('span', { class: 'sdot', 'aria-hidden': 'true' }), h('span', {}, x.name),
          h('span', { class: 'sr-only' }, ` (${x.state || 'pending'})`)))) : null)));
}

/** The log by stage: a heading with the stage name and time range, then monospace lines. Null when there is no data. */
export function logBlock(log) {
  const groups = (log || []).filter(g => g.lines && g.lines.length);
  if (!groups.length) return null;
  return h('section', { class: 'pdetail-log', 'data-part': 'log', 'aria-label': 'Log' }, groups.map(g => {
    const range = [g.from, g.to].filter(Boolean).filter((x, i, a) => a.indexOf(x) === i).join(' – ');
    return h('div', { class: 'log-group', 'data-stage': g.stage },
      h('h3', { class: 'log-heading' }, g.stage, range ? h('span', { class: 'muted small' }, ` ${range}`) : null),
      g.lines.map(l => {
        const parts = [l.time, l.actor, l.step, l.state, l.message].filter(Boolean).map(x => h('span', {}, x));
        if (l.href) parts.push(h('a', { href: l.href, target: '_blank', rel: 'noopener' }, 'link'));
        return h('div', { class: 'log-line mono small' }, parts.flatMap((p, i) => i ? [' ', p] : [p]));
      }));
  }));
}

/** The meta line, Result and Note come from the server (PipelineSteps.DetailOf), which leaves out fields with no data. */
function detailPanel(key, item, c, tabState, repaint) {
  const d = c.detail || { meta: [], result: '–', note: '–' };
  const meta = d.meta.map(p => p.href ? h('a', { href: p.href, target: '_blank', rel: 'noopener' }, p.text) : p.text);
  const joined = meta.flatMap((m, i) => i ? [' · ', m] : [m]);
  const { result, note } = d;
  const tab = (id, label) => h('button', { type: 'button', role: 'tab', class: tabState.tab === id ? 'active' : '', 'aria-selected': String(tabState.tab === id), 'data-tab': id,
    onclick: () => { tabState.tab = id; repaint(); } }, label);
  const stages = c.stages && c.stages.length ? c.stages : (c.dots || []).map(d => ({ step: d.step, state: d.state, subSteps: [] }));
  return h('section', { class: 'card pdetail', 'aria-label': `Task ${item.id}` },
    h('div', { class: 'pdetail-id' }, `#${item.id}`),
    h('h2', { class: 'pdetail-title' }, item.title),
    joined.length ? h('div', { class: 'pdetail-meta mono small muted' }, joined) : null,
    h('div', { class: 'tabs', role: 'tablist' }, tab('progress', 'Progress'), tab('diff', 'System diff')),
    tabState.tab === 'diff'
      ? h('p', { class: 'muted', 'data-part': 'diff-placeholder' }, 'System diff comes later.')
      : h('div', {}, stepper(stages),
        h('dl', { class: 'pdetail-rows' }, h('dt', {}, 'Result'), h('dd', { 'data-row': 'result' }, result), h('dt', {}, 'Note'), h('dd', { 'data-row': 'note' }, note)),
        logBlock(d.log)));
}

export async function render({ view, key, rest, go }) {
  if (rest[0] === 'job' && rest[1]) return renderJob({ view, key, go }, rest[1]);
  const holder = h('div', {}, loading());
  view.append(holder);
  let timer = null;
  const filters = { state: '', assignee: '', release: '', label: '' };
  let selected = null;
  const tabState = { tab: 'progress' };

  async function load() {
    const [tasks, runs] = await Promise.all([get(`${base(key)}/tasks`), get(`${base(key)}/runs`)]);
    clear(holder);
    holder.append(h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Pipeline'),
      h('p', {}, 'Every open task, from backlog to a draft a person merges.')),
    h('div', { class: 'row' },
      h('a', { href: link(key, 'inbox') }, 'Inbox'),
      h('a', { href: link(key, 'runs') }, 'Runs'),
      h('button', { type: 'button', onclick: () => load().catch(e => toast(e.message, true)) }, 'Refresh'),
      null)));

    if (!tasks.items.length) {
      holder.append(h('div', { class: 'card' }, emptyState(
        tasks.localTasks ? 'Add a task and it appears here. Triage it, put it in the pool, and the agent turns it into a draft pull request.' : 'No open work items were found in the tracker.',
        tasks.localTasks ? { label: 'Add your first task', href: link(key, 'tasks') } : { label: 'Check the task source', href: link(key, 'settings') },
        'Nothing in the pipeline yet')));
      return;
    }

    const list = h('div', { class: 'stack', 'data-part': 'pipeline-list' });
    const detail = h('div', { class: 'stack', 'data-part': 'pipeline-detail' });
    const paintDetail = () => {
      clear(detail);
      requestAnimationFrame(() => keepStickyFit(detail));
      const sel = selected && tasks.items.find(i => i.id === selected);
      if (sel) detail.append(detailPanel(key, sel, cardOf(tasks, sel), tabState, paintDetail));
      else detail.append(h('p', { class: 'muted pipeline-hint', 'data-part': 'pipeline-hint' }, 'Select a task'));
    };
    const paintList = () => {
      clear(list);
      const shown = tasks.items.filter(i => matches(i, cardOf(tasks, i), filters));
      if (!shown.length) list.append(emptyState('No work item matches these filters.', null, 'Nothing to show'));
      for (const item of shown) {
        list.append(pipelineCard(key, item, cardOf(tasks, item), selected === item.id, () => { selected = item.id; paintList(); paintDetail(); }));
      }
    };
    const filterBar = filtersBar(tasks, filters, paintList);
    paintList();
    paintDetail();
    holder.append(h('div', { class: 'pipeline-page' }, filterBar,
      h('div', { class: 'app-layout', 'data-page': 'pipeline' },
        h('div', { class: 'app-left' }, list), h('div', { class: 'app-right open' }, detail))));
    keepStickyFit(detail);
    return runs;
  }

  const first = await load();
  if (first && (first.status.busy || first.jobs.some(j => j.job.isActive))) timer = setInterval(() => load().catch(() => {}), 4000);
  return () => { if (timer) clearInterval(timer); };
}

// ---- one job, live ----
async function renderJob({ view, key }, id) {
  const holder = h('div', {}, loading());
  view.append(holder);
  let timer = null;
  async function load() {
    const data = await get(`${base(key)}/jobs/${encodeURIComponent(id)}`);
    const job = data.job;
    clear(holder);
    const steps = [
      ['Picked up', 'done', job.startedAt ? `started ${ago(job.startedAt)}` : 'queued'],
      ['Implement', job.isActive && !/gate|build|test|pr/i.test(job.step || '') ? 'active' : 'done', job.step || job.status],
      ['Build, tests and gate', /gate|build|test/i.test(job.step || '') ? 'active' : (job.isActive ? 'pending' : 'done'), ''],
      ['Draft PR', job.status === 'succeeded' ? 'done' : 'pending', ''],
    ];
    if (job.status === 'failed' || job.status === 'cancelled') steps.push([job.status === 'failed' ? 'Failed' : 'Cancelled', 'fail', job.message || '']);
    holder.append(
      h('p', { class: 'crumbs' }, h('a', { href: link(key, 'pipeline') }, 'Pipeline'), ' / task #', job.workItemId),
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, `Task #${job.workItemId}`),
        h('p', {}, `Job ${job.id}, ${job.isActive ? 'running for ' + duration(Date.now() - new Date(job.startedAt || job.createdAt)) : String(job.status).toLowerCase()}`)),
      job.isActive ? h('button', { type: 'button', class: 'danger', onclick: ev => stop(ev.currentTarget, key, job, load) }, 'Stop') : null),
      h('div', { class: 'split' },
        h('section', { class: 'card' }, h('h2', {}, 'Steps'), timeline(steps)),
        h('section', { class: 'card' }, h('h2', {}, 'Latest output'), data.logTail ? h('pre', { class: 'logtail' }, data.logTail) : h('p', { class: 'muted' }, 'No output yet.'))));
    return job;
  }
  let job;
  try { job = await load(); } catch (e) { clear(holder).append(h('div', { class: 'banner bad' }, e.message)); return; }
  if (job.isActive) timer = setInterval(() => load().catch(() => {}), 3000);
  return () => { if (timer) clearInterval(timer); };
}

async function stop(button, key, job, reload) {
  const ok = await ask({ title: `Stop job ${job.id}?`, danger: true, confirm: 'Stop this job',
    body: [h('p', {}, `This stops only the run for task #${job.workItemId}. Other jobs and the pool are not touched.`)] });
  if (!ok) return;
  await busy(button, async () => {
    try { await post(`${base(key)}/jobs/${encodeURIComponent(job.id)}/stop`); toast('Stopped.'); await reload(); } catch (e) { toast(e.message, true); }
  });
}

const ICON = { done: '✓', fail: '✕', active: '●', wait: '!', pending: '' };
/** steps: [name, status (done|fail|active|wait|pending), detail, duration text?] */
export function timeline(steps) {
  return h('ol', { class: 'timeline' }, steps.map(([name, status, detail, dur]) =>
    h('li', { class: `tl ${status}` }, h('span', { class: 'ico', 'aria-hidden': 'true' }, ICON[status] || ''),
      h('div', {}, h('div', { class: 'name' }, name, dur ? h('span', { class: 'dur' }, dur) : null, h('span', { class: 'sr-only' }, ` (${status})`)),
        detail ? h('div', { class: 'detail' }, detail) : null))));
}
