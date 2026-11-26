import { post } from './api.js';
import { h, clear, toast, busy, badge } from './dom.js';
import { STEPS, STEP_COUNT, runChecks, loadDefaults, renderChecks } from './setupform.js';

function emptyDraft() {
  return {
    key: '', displayName: '', repositoryFolder: '', baseBranch: 'main', buildCommand: 'dotnet build', testCommand: 'dotnet test',
    writablePaths: ['src/**', 'tests/**'], riskPaths: [], context: [], taskSource: 'local',
    ado: { organizationUrl: '', project: '', repository: '', query: '', auth: 'pat', tokenVariable: 'AUTOPILOT_ADO_PAT', tenantVariable: 'AUTOPILOT_ADO_TENANT_ID', clientIdVariable: 'AUTOPILOT_ADO_CLIENT_ID', clientSecretVariable: 'AUTOPILOT_ADO_CLIENT_SECRET' },
    identity: { name: 'Dev Autopilot', email: 'autopilot@localhost' },
    triage: { model: null, maxToolCalls: 20 }, implementer: { model: null, maxToolCalls: 60 }, reviewer: { model: null, maxToolCalls: 30 },
    maxFilesChanged: 6, maxSourceLinesChanged: 150, maxTestLinesChanged: 300, requireTestChange: true, trust: 'ask',
  };
}

export async function render({ view, go, refreshState }) {
  let draft = emptyDraft();
  let step = 1;
  const passed = new Set();
  const last = new Map(); // the newest check result of each step, shown again when you come back
  const holder = h('div');
  view.append(holder);

  async function onFolder() {
    try {
      const found = await loadDefaults(draft.repositoryFolder, draft.baseBranch);
      const keep = { key: draft.key, displayName: draft.displayName, taskSource: draft.taskSource, ado: draft.ado };
      draft = { ...found, ...(keep.key && keep.key !== found.key ? keep : { taskSource: draft.taskSource, ado: draft.ado }) };
      passed.clear();
      last.clear();
      toast('Found the build and test commands and the paths for this folder. Check them in the next steps.');
      draw();
    } catch (e) { toast(e.message, true); }
  }

  function draw() {
    clear(holder);
    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Add a project'),
        h('p', {}, 'Each step explains what it is for, checks the value live, and says how to fix a problem.'))),
      h('ol', { class: 'steps' }, [...STEPS.map(s => s.title), 'Summary'].map((title, i) =>
        h('li', { class: i + 1 === step ? 'current' : passed.has(i + 1) ? 'done' : '' }, `${i + 1}. ${title}`))));
    if (step <= STEPS.length) drawStep(STEPS[step - 1]); else drawSummary();
  }

  function drawStep(def) {
    const results = h('div', { 'aria-live': 'polite' });
    if (last.has(def.id)) results.append(h('p', {}, last.get(def.id).passed ? badge('Last check passed', 'ok') : badge('Last check failed', 'bad')), renderChecks(last.get(def.id)));
    const next = h('button', { class: 'primary', type: 'button' }, step === STEPS.length ? 'Check and go to the summary' : 'Check and continue');
    const checkOnly = h('button', { type: 'button' }, 'Check this step');
    const run = async () => {
      const result = await runChecks(def.id, draft, results);
      if (result) last.set(def.id, result);
      if (result?.passed) passed.add(def.id); else passed.delete(def.id);
      return result;
    };
    checkOnly.addEventListener('click', () => busy(checkOnly, run));
    next.addEventListener('click', () => busy(next, async () => {
      const result = await run();
      if (result?.passed) { step += 1; draw(); window.scrollTo({ top: 0 }); }
    }));
    holder.append(h('section', { class: 'card stack' },
      h('h2', {}, `Step ${def.id}: ${def.title}`),
      h('div', { class: 'purpose' }, def.purpose),
      def.build(draft, { mode: 'new', onFolder }),
      h('div', { class: 'row' },
        step > 1 ? h('button', { type: 'button', onclick: () => { step -= 1; draw(); } }, 'Back') : null, checkOnly, next),
      results));
  }

  async function drawSummary() {
    const results = h('div', { 'aria-live': 'polite' });
    const create = h('button', { class: 'primary', type: 'button', disabled: true }, 'Create the project');
    holder.append(h('section', { class: 'card stack' },
      h('h2', {}, 'Summary'),
      h('div', { class: 'purpose' }, 'Every required check runs once more on the whole setup. The project can be created only when none of them fails.'),
      summaryList(),
      h('div', { class: 'row' }, h('button', { type: 'button', onclick: () => { step -= 1; draw(); } }, 'Back'), create),
      results));
    const result = await runChecks(6, draft, results);
    create.disabled = !result?.passed;
    create.addEventListener('click', () => busy(create, async () => {
      try {
        await post('/projects', draft);
        await refreshState();
        toast('Project created.');
        go(`p/${encodeURIComponent(draft.key)}/tasks`);
      } catch (e) {
        clear(results);
        results.append(h('div', { class: 'banner bad' }, e.message), ...(e.problems || []).slice(1).map(p => h('p', {}, p)));
      }
    }));
  }

  function summaryList() {
    const rows = [
      ['Folder', draft.repositoryFolder], ['Base branch', draft.baseBranch], ['Key', draft.key],
      ['Build', draft.buildCommand], ['Test', draft.testCommand],
      ['Writable paths', draft.writablePaths.join(', ')],
      ['Task source', draft.taskSource === 'local' ? 'Local tasks' : `Azure DevOps (${draft.ado.organizationUrl}, ${draft.ado.project})`],
      ['Agent identity', `${draft.identity.name} <${draft.identity.email}>`],
      ['Merging', 'Always by a person'],
    ];
    return h('dl', { style: 'display:grid;grid-template-columns:max-content 1fr;gap:.25rem 1rem;margin:0' },
      rows.flatMap(([k, v]) => [h('dt', { class: 'muted' }, k), h('dd', { style: 'margin:0;word-break:break-word' }, v || '-')]));
  }

  draw();
}
