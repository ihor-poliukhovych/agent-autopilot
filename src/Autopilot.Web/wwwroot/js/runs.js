import { get, post } from './api.js';
import { timeline, duration } from './pipeline.js';
import { h, clear, toast, badge, ago, emptyState, busy, ask, put } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

export async function render(ctx) {
  return ctx.rest[0] ? renderDetail(ctx, ctx.rest[0]) : renderOverview(ctx);
}

const verdictKind = v => v === 'passed' ? 'ok' : v === 'rejected' ? 'bad' : '';
const decisionText = d => d ? (d === 'merged' ? 'merged by a person' : 'rejected by a person') : null;

async function renderOverview({ view, key }) {
  const holder = h('div');
  view.append(holder);
  let timer = null;

  async function load() {
    const data = await get(`${base(key)}/runs`);
    clear(holder);
    const s = data.stats;
    holder.append(h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Runs'),
      h('p', {}, 'What the agent did, what the gate said, and what a person decided.'))));

    if (data.status.busy) {
      holder.append(h('div', { class: 'banner info' }, `A run is active: ${data.status.owner} (process ${data.status.pid}).`));
    }

    const active = data.jobs.filter(j => j.job.isActive);
    for (const j of active) holder.append(jobCard(key, j, load));

    holder.append(h('section', { class: 'card' },
      h('h2', {}, 'How often a person agreed with the gate'),
      s.decided === 0
        ? h('p', { class: 'muted' }, 'No decision recorded yet. Open a finished run and say whether you merged or rejected it; the agreement rate is what trust levels will be based on.')
        : [
          h('div', { class: 'row' }, h('div', { class: 'stat' }, `${Math.round(s.agreementRate * 100)}%`),
            h('span', { class: 'muted' }, `${s.agreed} of ${s.decided} decisions matched the gate's verdict`)),
          h('div', { class: 'meter', role: 'img', 'aria-label': `${Math.round(s.agreementRate * 100)} percent agreement` }, h('div', { style: `width:${s.agreementRate * 100}%` })),
          h('p', { class: 'small muted' }, `Gate too lenient (passed, person rejected): ${s.gateTooLenient}. Gate too strict (rejected, person merged anyway): ${s.gateTooStrict}.`),
        ],
      h('p', { class: 'small muted' }, `${s.totalRuns} runs in all: gate passed ${s.gatePassed}, gate rejected ${s.gateRejected}.`)));

    holder.append(h('section', { class: 'card' }, h('h2', {}, 'History'),
      !data.runs.length ? emptyState('A run is one attempt by the agent. Start one from a task or from the pool.', { label: 'Go to the tasks', href: `#/p/${encodeURIComponent(key)}/tasks` }, 'No runs yet') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
        h('thead', {}, h('tr', {}, ['Run', 'Task', 'Outcome', 'Gate', 'Person', 'When'].map(t => h('th', {}, t)))),
        h('tbody', {}, data.runs.map(r => h('tr', {},
          h('td', { 'data-label': 'Run' }, h('a', { href: `#/p/${encodeURIComponent(key)}/runs/${r.id}` }, '#' + r.id)),
          h('td', { 'data-label': 'Task' }, '#' + r.workItemId),
          h('td', { 'data-label': 'Outcome' }, r.outcome),
          h('td', { 'data-label': 'Gate' }, r.gateVerdict ? badge(r.gateVerdict, verdictKind(r.gateVerdict)) : h('span', { class: 'muted' }, 'none')),
          h('td', { 'data-label': 'Person' }, r.humanDecision ? badge(r.humanDecision, r.humanDecision === 'merged' ? 'ok' : 'bad') : h('span', { class: 'muted' }, 'not decided')),
          h('td', { 'data-label': 'When' }, ago(r.finishedAt)))))))))
    return data;
  }

  const first = await load();
  if (first.status.busy || first.jobs.some(j => j.job.isActive)) timer = setInterval(() => load().catch(() => {}), 3000);
  return () => { if (timer) clearInterval(timer); };
}

function jobCard(key, j, reload) {
  const job = j.job;
  return h('section', { class: 'card stack' },
    h('div', { class: 'row between' },
      h('h2', {}, `Running: task #${job.workItemId}`),
      h('button', { type: 'button', class: 'danger', onclick: async ev => {
        const button = ev.currentTarget; // currentTarget is gone after the first await
        const ok = await ask({
          title: `Stop job ${job.id}?`, danger: true, confirm: 'Stop this job',
          body: [h('p', {}, `This stops only the run for task #${job.workItemId}. Other jobs and the pool are not touched.`)],
        });
        if (!ok) return;
        await busy(button, async () => {
          try { await post(`${base(key)}/jobs/${encodeURIComponent(job.id)}/stop`); toast('Stopped.'); await reload(); } catch (e) { toast(e.message, true); }
        });
      } }, 'Stop')),
    h('p', {}, h('span', { class: 'spinner', 'aria-hidden': 'true' }), ' Current step: ', h('b', {}, job.step || job.status), ' ', h('span', { class: 'muted small' }, `started ${ago(job.startedAt || job.createdAt)}`)),
    j.logTail ? h('details', { open: true }, h('summary', {}, 'Latest output'), h('pre', {}, j.logTail)) : null);
}

async function renderDetail({ view, key }, id) {
  const holder = h('div');
  view.append(holder);
  await show(holder, key, id);
}

async function show(holder, key, id) {
  clear(holder);
  let detail;
  try { detail = await get(`${base(key)}/runs/${encodeURIComponent(id)}`); }
  catch (e) { holder.append(h('div', { class: 'banner bad' }, e.message)); return; }
  const r = detail.run;
  const decide = async (decision) => {
    try { await post(`${base(key)}/runs/${r.id}/decision`, { decision }); toast('Recorded.'); await show(holder, key, id); }
    catch (e) { toast(e.message, true); }
  };
  put(holder,
    h('p', { class: 'crumbs' }, h('a', { href: `#/p/${encodeURIComponent(key)}/pipeline` }, 'Pipeline'), ' / ', h('a', { href: `#/p/${encodeURIComponent(key)}/runs` }, 'Runs'), ` / #${r.id}`),
    h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, `Run #${r.id} for task #${r.workItemId}`),
      h('p', {}, `${r.outcome}${r.branch ? ` on branch ${r.branch}` : ''}, ${ago(r.finishedAt)}`)),
      h('div', { class: 'row' }, r.gateVerdict ? badge(`gate ${r.gateVerdict}`, verdictKind(r.gateVerdict)) : badge('no gate verdict'),
        r.humanDecision ? badge(decisionText(r.humanDecision), r.humanDecision === 'merged' ? 'ok' : 'bad') : null)),
    r.message ? h('div', { class: 'banner info' }, r.message) : null,
    detail.base ? h('p', { class: 'muted' }, 'Base: ', h('code', {}, baseText(detail.base))) : null,
    h('div', { class: 'split' },
      h('section', { class: 'card' }, h('h2', {}, 'Steps'), timeline(runSteps(detail))),
      h('div', {}, decisionCard(r, decide), detail.diff ? h('p', {}, h('a', { class: 'btn', href: '#diff', onclick: ev => { ev.preventDefault(); document.getElementById('diff')?.scrollIntoView(); } }, 'Jump to the diff')) : null)),
    gateCard(detail),
    detail.report ? h('section', { class: 'card' }, h('h2', {}, r.gateVerdict === 'rejected' ? 'Why it was rejected' : 'Draft description'), h('pre', {}, detail.report)) : null,
    detail.diff ? h('section', { class: 'card', id: 'diff' }, h('h2', {}, 'Diff'), diffView(detail.diff), detail.diffTruncated ? h('p', { class: 'muted' }, 'The diff is shortened here; the full patch is in the run folder.') : null) : null,
    detail.gateLog ? h('details', { class: 'card' }, h('summary', {}, 'Build and test log (end)'), h('pre', {}, detail.gateLog)) : null);
}

/** "main <full commit>" as "main (abcdef123456)". */
function baseText(text) {
  const [branch, commit] = text.split(/\s+/);
  return commit ? `${branch} (${commit.slice(0, 12)})` : text;
}

function gateCard(detail) {
  const g = detail.gate;
  if (!g) return h('section', { class: 'card' }, h('h2', {}, 'Gate report'), h('p', { class: 'muted' }, detail.run.gateVerdict ? `The gate said ${detail.run.gateVerdict}; the detailed report is not stored for this run.` : 'This run has no gate report (it did not reach the gate).'));
  const yes = v => v ? badge('yes', 'ok') : badge('no', 'bad');
  return h('section', { class: 'card stack' }, h('h2', {}, 'Gate report'),
    h('div', { class: 'row' }, badge(`outcome: ${g.outcome}`, String(g.outcome).toLowerCase().includes('pass') ? 'ok' : 'bad'), g.highRisk ? badge('high risk: needs a person', 'warn') : null),
    h('p', {}, 'Build passed ', yes(g.buildSucceeded), '  Tests passed ', yes(g.testsSucceeded),
      g.testCountsKnown ? `  Tests: ${g.testCountBefore} before, ${g.testCountAfter} after` : '', g.skipsAdded ? `  Skips added: ${g.skipsAdded}` : ''),
    h('p', {}, `${g.filesChanged} files changed, ${g.sourceLinesChanged} source lines, ${g.testLinesChanged} test lines.`),
    (g.findings || []).length ? h('ul', {}, g.findings.map(f => h('li', {}, h('code', {}, f.ruleId || f.rule || 'rule'), ' ', f.message || ''))) : h('p', { class: 'muted' }, 'No findings.'),
    (g.files || []).length ? h('details', {}, h('summary', {}, 'Files'), h('ul', {}, g.files.map(f => h('li', {}, `${f.path} (+${f.added} -${f.removed}${f.test ? ', test' : ''})`)))) : null);
}

function diffView(text) {
  const pre = h('pre', { class: 'diff' });
  for (const line of text.split('\n')) {
    let cls = '';
    if (line.startsWith('diff --git')) cls = 'file';
    else if (line.startsWith('@@')) cls = 'hunk';
    else if (line.startsWith('+') && !line.startsWith('+++')) cls = 'add';
    else if (line.startsWith('-') && !line.startsWith('---')) cls = 'del';
    pre.append(h('span', { class: cls }, line + '\n'));
  }
  return pre;
}

function decisionCard(r, decide) {
  return h('section', { class: 'card stack' }, h('h2', {}, 'Your decision'),
    h('p', { class: 'muted' }, 'Merging is always done by a person, in your own tool. Record here what you did, so the gate can be compared with your judgement.'),
    h('div', { class: 'row' },
      h('button', { type: 'button', class: r.humanDecision ? '' : 'primary', onclick: () => decide('merged') }, 'I merged it'),
      h('button', { type: 'button', onclick: () => decide('rejected') }, 'I rejected it')));
}

/** The run as steps: what happened, what the gate said, what a person did. Only what the run record knows. */
function runSteps(detail) {
  const r = detail.run, g = detail.gate;
  const took = duration(new Date(r.finishedAt) - new Date(r.startedAt));
  const steps = [['Picked up', 'done', `Task #${r.workItemId} started ${ago(r.startedAt)}`]];
  const implemented = !!(g || r.branch);
  steps.push(['Implement', implemented ? 'done' : 'fail', r.message || r.outcome, took]);
  if (g) {
    steps.push(['Build and tests', g.buildSucceeded && g.testsSucceeded ? 'done' : 'fail',
      `Build ${g.buildSucceeded ? 'passed' : 'failed'}, tests ${g.testsSucceeded ? 'passed' : 'failed'}${g.testCountsKnown ? ` (${g.testCountBefore} before, ${g.testCountAfter} after)` : ''}`]);
    steps.push(['Gate', r.gateVerdict === 'passed' ? 'done' : 'fail', `${r.gateVerdict || g.outcome}${g.highRisk ? ', high risk: needs a person' : ''}; ${g.filesChanged} files, ${g.sourceLinesChanged} source lines`]);
  } else if (r.gateVerdict) steps.push(['Gate', r.gateVerdict === 'passed' ? 'done' : 'fail', `Verdict: ${r.gateVerdict}${r.highRisk ? ', high risk: needs a person' : ''}`]);
  else steps.push(['Gate', 'pending', 'This run did not reach the gate.']);
  steps.push(['Draft PR', r.wouldMerge ? 'done' : 'pending', r.branch ? `Branch ${r.branch}` : 'No draft was produced.']);
  steps.push(['Person decides', r.humanDecision ? (r.humanDecision === 'merged' ? 'done' : 'fail') : (r.wouldMerge ? 'wait' : 'pending'),
    r.humanDecision ? decisionText(r.humanDecision) : (r.wouldMerge ? 'Waiting for you.' : '')]);
  return steps;
}
