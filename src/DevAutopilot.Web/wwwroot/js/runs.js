import { get, post } from './api.js';
import { h, clear, toast, badge, ago, emptyState, busy, ask } from './dom.js';

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
      !data.runs.length ? emptyState('No runs yet.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
        h('thead', {}, h('tr', {}, ['Run', 'Task', 'Outcome', 'Gate', 'Person', 'When'].map(t => h('th', {}, t)))),
        h('tbody', {}, data.runs.map(r => h('tr', {},
          h('td', { 'data-label': 'Run' }, h('a', { href: `#/p/${encodeURIComponent(key)}/runs/${r.id}` }, '#' + r.id)),
          h('td', { 'data-label': 'Task' }, '#' + r.workItemId),
          h('td', { 'data-label': 'Outcome' }, r.outcome),
          h('td', { 'data-label': 'Gate' }, r.gateVerdict ? badge(r.gateVerdict, verdictKind(r.gateVerdict)) : h('span', { class: 'muted' }, 'none')),
          h('td', { 'data-label': 'Person' }, r.humanDecision ? badge(r.humanDecision, r.humanDecision === 'merged' ? 'ok' : 'bad') : h('span', { class: 'muted' }, 'not decided')),
          h('td', { 'data-label': 'When' }, ago(r.finishedAt))))))))));
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
        const ok = await ask({
          title: `Stop job ${job.id}?`, danger: true, confirm: 'Stop this job',
          body: [h('p', {}, `This stops only the run for task #${job.workItemId}. Other jobs and the pool are not touched.`)],
        });
        if (!ok) return;
        await busy(ev.currentTarget, async () => {
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
  holder.append(
    h('p', {}, h('a', { href: `#/p/${encodeURIComponent(key)}/runs` }, '← All runs')),
    h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, `Run #${r.id} for task #${r.workItemId}`),
      h('p', {}, `${r.outcome}${r.branch ? ` on branch ${r.branch}` : ''}, ${ago(r.finishedAt)}`)),
      h('div', { class: 'row' }, r.gateVerdict ? badge(`gate ${r.gateVerdict}`, verdictKind(r.gateVerdict)) : badge('no gate verdict'),
        r.humanDecision ? badge(decisionText(r.humanDecision), r.humanDecision === 'merged' ? 'ok' : 'bad') : null)),
    r.message ? h('div', { class: 'banner' }, r.message) : null,
    h('section', { class: 'card stack' }, h('h2', {}, 'Your decision'),
      h('p', { class: 'muted' }, 'Merging is always done by a person, in your own tool. Record here what you did, so the gate can be compared with your judgement.'),
      h('div', { class: 'row' },
        h('button', { type: 'button', onclick: () => decide('merged') }, 'I merged it'),
        h('button', { type: 'button', onclick: () => decide('rejected') }, 'I rejected it'))),
    gateCard(detail),
    detail.report ? h('section', { class: 'card' }, h('h2', {}, r.gateVerdict === 'rejected' ? 'Why it was rejected' : 'Draft description'), h('pre', {}, detail.report)) : null,
    detail.diff ? h('section', { class: 'card' }, h('h2', {}, 'Diff'), diffView(detail.diff), detail.diffTruncated ? h('p', { class: 'muted' }, 'The diff is shortened here; the full patch is in the run folder.') : null) : null,
    detail.gateLog ? h('details', { class: 'card' }, h('summary', {}, 'Build and test log (end)'), h('pre', {}, detail.gateLog)) : null);
}

function gateCard(detail) {
  const g = detail.gate;
  if (!g) return h('section', { class: 'card' }, h('h2', {}, 'Gate report'), h('p', { class: 'muted' }, 'This run has no gate report (it did not reach the gate).'));
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
