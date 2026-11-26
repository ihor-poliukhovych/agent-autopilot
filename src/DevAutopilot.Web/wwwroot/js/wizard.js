import { post } from './api.js';
import { h, clear, toast, busy, badge, field, ask } from './dom.js';
import { STEPS, runChecks, analyzeFolder, renderChecks, evidenceBox, needsBox, identityFields } from './setupform.js';

// The wizard has three views of one draft:
//   folder   - the only thing a person has to do: choose the repository folder and press Analyze.
//   review   - one page: every detected value (editable, with its evidence), what is still unknown, the live
//              checks, and the Create button.
//   advanced - the same settings one step at a time, each step with its own check.
export async function render({ view, go, refreshState }) {
  let draft = null;
  let analysis = null;
  let mode = 'folder';
  let step = 1;
  let folderText = '';
  const passed = new Set();
  const last = new Map(); // the newest check result of each step, shown again when you come back
  const holder = h('div');
  view.append(holder);

  const draw = () => {
    clear(holder);
    if (mode === 'folder') drawFolder();
    else if (mode === 'review') drawReview();
    else drawAdvanced();
    window.scrollTo({ top: 0 });
  };

  // ----- 1. the folder -----
  function drawFolder() {
    const input = h('input', { type: 'text', class: 'mono', placeholder: 'C:\\code\\my-app  or  /home/me/my-app', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Repository folder' });
    input.value = folderText;
    const message = h('div', { 'aria-live': 'polite' });
    const analyze = h('button', { class: 'primary', type: 'button' }, 'Analyze the folder');
    const run = () => busy(analyze, async () => {
      folderText = input.value.trim();
      clear(message);
      if (!folderText) { message.append(h('div', { class: 'banner bad' }, 'Type or paste the full path of the repository folder first.')); return; }
      message.append(h('p', {}, h('span', { class: 'spinner' }), ' Reading the folder…'));
      try {
        const found = await analyzeFolder(folderText);
        draft = found.draft;
        analysis = found.analysis;
        passed.clear();
        last.clear();
        mode = 'review';
        draw();
      } catch (e) {
        clear(message);
        message.append(h('div', { class: 'banner bad' }, e.message));
      }
    });
    analyze.addEventListener('click', run);
    input.addEventListener('keydown', ev => { if (ev.key === 'Enter') run(); });
    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Add a project'),
        h('p', {}, 'Choose the repository folder. Dev Autopilot reads it and fills in every setting for you to review.'))),
      h('section', { class: 'card stack' },
        h('h2', {}, 'Step 1: the repository folder'),
        h('div', { class: 'purpose' }, 'Only reads the folder: it never writes into it, uses no model and sends nothing anywhere. A folder inside a repository is treated as the whole repository.'),
        field('Repository folder', input, 'The top folder of the git repository (the one that contains .git).'),
        h('div', { class: 'row' }, analyze),
        message));
    input.focus();
  }

  // ----- 2. one page to review and finish -----
  function drawReview() {
    const results = h('div', { 'aria-live': 'polite' });
    const create = h('button', { class: 'primary', type: 'button', disabled: true }, 'Create the project');
    const recheck = h('button', { type: 'button' }, 'Run the checks again');
    const check = async () => {
      const result = await runChecks(6, draft, results);
      create.disabled = !result?.passed;
      return result;
    };
    recheck.addEventListener('click', () => busy(recheck, check));
    create.addEventListener('click', () => busy(create, async () => {
      const result = await check();
      if (!result?.passed) return;
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

    const section = (title, purpose, group, body) => h('section', { class: 'card stack' },
      h('h2', {}, title), h('div', { class: 'purpose' }, purpose), evidenceBox(analysis, group), body);
    const steps = Object.fromEntries(STEPS.map(s => [s.id, s]));
    const build = id => steps[id].build(draft, { mode: 'review' });

    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Review the project'),
        h('p', {}, 'Everything below was found in the folder. Change anything that is wrong, then create the project once the checks pass.'))),
      h('section', { class: 'card stack' }, h('h2', {}, 'What still needs you'), needsBox(analysis)),
      section('Project', 'The repository, the branch the agent starts from, and the name of the project.', 'folder', identityFields(draft)),
      section('Build and test', 'The commands that must pass on the base branch. The checks below run them on a clean copy of that branch, like the merge gate, so this can take a few minutes.', 'commands', build(2)),
      section('Paths', 'Where the agent may write, which changes always need a close look, and the rule documents the agent reads on every run.', 'paths', build(3)),
      section('Task source', 'Where the work comes from. Secrets are never typed here: only the names of environment variables that hold them.', 'source', build(4)),
      h('section', { class: 'card stack' },
        h('h2', {}, 'Checks'),
        h('div', { class: 'purpose' }, 'Every required check runs on these values: the folder, the base branch, the build and the tests on a clean checkout, the paths, the task source and its connection. The project can be created only when none fails.'),
        h('div', { class: 'row' }, recheck, create),
        results),
      h('div', { class: 'row' },
        h('button', { type: 'button', onclick: () => { mode = 'advanced'; step = 1; draw(); } }, 'Advanced: go through the settings step by step'),
        h('button', { type: 'button', onclick: async () => {
          if (await ask({ title: 'Start again?', body: 'Your changes on this page are dropped and the folder is analyzed again.', confirm: 'Start again' })) { mode = 'folder'; draw(); }
        } }, 'Choose another folder')));
    busy(recheck, check);
  }

  // ----- the advanced view: the old step by step wizard, with the detected evidence on top -----
  function drawAdvanced() {
    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Add a project: step by step'),
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
      if (result?.passed) { step += 1; draw(); }
    }));
    const group = { 1: 'folder', 2: 'commands', 3: 'paths', 4: 'source' }[def.id];
    holder.append(h('section', { class: 'card stack' },
      h('h2', {}, `Step ${def.id}: ${def.title}`),
      h('div', { class: 'purpose' }, def.purpose),
      group ? evidenceBox(analysis, group) : null,
      def.id === 1 ? identityFields(draft) : def.build(draft, { mode: 'new' }),
      h('div', { class: 'row' },
        step > 1 ? h('button', { type: 'button', onclick: () => { step -= 1; draw(); } }, 'Back') : h('button', { type: 'button', onclick: () => { mode = 'review'; draw(); } }, 'Back to the review page'),
        checkOnly, next),
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
