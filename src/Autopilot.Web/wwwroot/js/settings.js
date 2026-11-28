import { get, put } from './api.js';
import { h, clear, toast, busy, badge, field } from './dom.js';
import { connectAzureDevOps } from './connect.js';
import { importPanel } from './importpanel.js';
import { STEPS, trustField, runChecks, gateLimitProblems } from './setupform.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

export async function render({ view, key, refreshState }) {
  const draft = await get(`${base(key)}/setup`);
  const mode = 'edit';
  const original = { ...draft };

  view.append(
    h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Project settings'),
      h('p', {}, 'The same steps as the setup wizard. Each section can be checked on its own; nothing is saved until you press Save.'))),
    h('section', { class: 'card' },
      h('h2', {}, 'Merging'),
      h('p', {}, badge('merged by a person', 'ok'), ' The agent never merges. It ends every run with a draft; a person reviews and merges it in their own tool. This cannot be switched off.')),
    h('section', { class: 'card' },
      h('h2', {}, 'Task source'),
      h('p', {}, draft.taskSource === 'local'
        ? 'Local tasks: written and kept in this tool.'
        : `Azure DevOps: ${draft.ado.organizationUrl} / ${draft.ado.project}. Only names of environment variables are stored; the secrets are not.`),
      draft.taskSource === 'local' ? connectAzureDevOps(draft) : h('div', { class: 'stack' }, h('p', { class: 'muted small' }, 'Change it in the section "Task source" below.'), importPanel(key))));

  for (const def of STEPS) {
    const results = h('div', { 'aria-live': 'polite' });
    const checkBtn = h('button', { type: 'button' }, 'Check');
    checkBtn.addEventListener('click', () => busy(checkBtn, () => runChecks(def.id, draft, results, { projectKey: key })));
    view.append(h('section', { class: 'card stack' },
      h('h2', {}, def.title), h('div', { class: 'purpose' }, def.purpose), def.build(draft, { mode }),
      def.id === 5 ? trustField(draft) : null,
      h('div', {}, checkBtn), results));
  }

  const saveResult = h('div');
  const save = h('button', { class: 'primary', type: 'button' }, 'Save settings');
  save.addEventListener('click', () => busy(save, async () => {
    clear(saveResult);
    try {
      const limitProblems = gateLimitProblems(draft, original);
      if (limitProblems.length) {
        saveResult.append(h('div', { class: 'banner bad' }, h('b', {}, 'Not saved. '), ...limitProblems.map(p => h('div', {}, p))));
        return;
      }
      await put(`${base(key)}/setup`, draft);
      await refreshState();
      toast('Saved.');
    } catch (e) {
      saveResult.append(h('div', { class: 'banner bad' }, h('b', {}, 'Not saved. '), ...(e.problems || [e.message]).map(p => h('div', {}, p))));
    }
  }));
  view.append(h('div', { class: 'card row' }, save, saveResult));

  await contextEditor(view, key, draft);
}

async function contextEditor(view, key, draft) {
  const card = h('section', { class: 'card stack' }, h('h2', {}, 'Context documents'),
    h('p', { class: 'muted' }, 'Rules and background the agent reads before every run, such as architecture notes and conventions. The agent can read them but never change them. They are read from the base branch, so commit them there.'));
  view.append(card);
  const files = await get(`${base(key)}/context`);
  const select = h('select', { 'aria-label': 'Context file' }, [h('option', { value: '' }, files.length ? 'Choose a file' : 'No context files yet'), ...files.map(f => h('option', { value: f }, f))]);
  const newPath = h('input', { type: 'text', class: 'mono', placeholder: 'docs/agent-rules.md', 'aria-label': 'New file path' });
  const editor = h('textarea', { class: 'mono', rows: 14, spellcheck: 'false', 'aria-label': 'File text' });
  const save = h('button', { class: 'primary', type: 'button', disabled: true }, 'Save document');
  let current = null;

  select.addEventListener('change', async () => {
    if (!select.value) return;
    await open(select.value);
  });
  async function open(path) {
    try { const f = await get(`${base(key)}/context/file?path=${encodeURIComponent(path)}`); current = path; editor.value = f.text; save.disabled = false; }
    catch (e) { toast(e.message, true); }
  }
  card.append(
    h('div', { class: 'row' }, select, h('span', { class: 'muted' }, 'or a new file'), newPath, h('button', { type: 'button', onclick: async () => {
      if (!newPath.value.trim()) return;
      current = newPath.value.trim(); editor.value = ''; save.disabled = false; editor.focus();
    } }, 'Start a new document')),
    editor, h('div', {}, save));
  save.addEventListener('click', () => busy(save, async () => {
    try { await put(`${base(key)}/context/file`, { path: current, text: editor.value }); toast(`Saved ${current}.`); }
    catch (e) { toast((e.problems || [e.message]).join(' '), true); }
  }));
  if (!draft.context.length) card.append(h('p', { class: 'hint' }, 'To list a file here, first add its path or folder under "Context documents" in the Paths section and save.'));
}
