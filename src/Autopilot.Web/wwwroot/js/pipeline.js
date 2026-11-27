import { get, post } from './api.js';
import { h, clear, toast, badge, ago, emptyState, loading, busy, ask } from './dom.js';

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

function waitingPanel(key, items) {
  if (!items.length) return null;
  return h('section', { class: 'card waiting', 'aria-label': 'Waiting for a person' },
    h('div', { class: 'row between' }, h('h2', {}, `Waiting for a person (${items.length})`), h('a', { class: 'btn sm', href: link(key, 'needs') }, 'Answer questions')),
    h('ul', {}, items.slice(0, 5).map(i => h('li', {},
      h('b', {}, `#${i.id}`), h('span', {}, i.title), h('span', { class: 'why' }, i.why)))));
}

export async function render({ view, key, rest, go }) {
  if (rest[0] === 'job' && rest[1]) return renderJob({ view, key, go }, rest[1]);
  const holder = h('div', {}, loading());
  view.append(holder);
  let timer = null;

  async function load() {
    const [tasks, runs, needs] = await Promise.all([
      get(`${base(key)}/tasks`), get(`${base(key)}/runs`), get(`${base(key)}/needs-human`).catch(() => [])]);
    clear(holder);
    holder.append(h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Pipeline'),
      h('p', {}, 'Every open task, from backlog to a draft a person merges.')),
    h('div', { class: 'row' },
      h('a', { href: link(key, 'needs') }, 'Needs a person'),
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

    const lanes = Object.fromEntries(STAGES.map(([id]) => [id, []]));
    const waiting = [];
    for (const item of tasks.items) {
      const jobId = tasks.activeJobs[item.id];
      const job = jobId ? (runs.jobs.find(j => j.job.id === jobId) || {}).job || { id: jobId, status: 'running' } : null;
      const stage = stageOf(item, job);
      lanes[stage].push([item, job, stage]);
      const needsInfo = needs.find(n => n.id === item.id);
      if (needsInfo) waiting.push({ id: item.id, title: item.title, why: needsInfo.question });
      else if (stage === 'pr') waiting.push({ id: item.id, title: item.title, why: 'Draft is ready: review it, merge or reject, and record your decision.' });
      else if (stage === 'gate') waiting.push({ id: item.id, title: item.title, why: 'The gate rejected this run: read why, then retry or reject.' });
    }
    const fun = funnel(runs.stats, runs.runs);
    holder.append(waitingPanel(key, waiting), fun,
      h('div', { class: 'board', role: 'list' }, STAGES.map(([id, label]) => h('section', { class: `lane s-${id}${lanes[id].length ? '' : ' is-empty'}`, role: 'listitem', 'aria-label': label },
        h('div', { class: 'lane-head' }, h('span', {}, h('span', { class: 'bar' }), label), h('span', { class: 'n' }, String(lanes[id].length))),
        lanes[id].length ? lanes[id].map(([item, job, stage]) => card(key, item, job, stage)) : h('div', { class: 'lane-empty' }, 'Empty')))));
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
