// The setup steps, shared by the add-project wizard (one step at a time) and the settings page (all at once).
import { get, post } from './api.js';
import { h, clear, field, badge, toast, busy } from './dom.js';

const getPath = (obj, path) => path.split('.').reduce((o, k) => (o == null ? o : o[k]), obj);
function setPath(obj, path, value) {
  const keys = path.split('.');
  const last = keys.pop();
  keys.reduce((o, k) => o[k], obj)[last] = value;
}

function text(draft, path, { mono = false, placeholder = '', type = 'text', onchange } = {}) {
  const input = h('input', { type, class: mono ? 'mono' : '', placeholder, autocomplete: 'off', spellcheck: 'false' });
  input.value = getPath(draft, path) ?? '';
  input.addEventListener('input', () => { setPath(draft, path, input.value); if (onchange) onchange(input.value); });
  return input;
}
function lines(draft, path, rows = 5) {
  const area = h('textarea', { class: 'mono', rows, spellcheck: 'false' });
  area.value = (getPath(draft, path) || []).join('\n');
  area.addEventListener('input', () => setPath(draft, path, area.value.split('\n').map(s => s.trim()).filter(Boolean)));
  return area;
}
function number(draft, path, min = 1) {
  const input = h('input', { type: 'number', min, step: 1 });
  input.value = getPath(draft, path);
  input.addEventListener('input', () => setPath(draft, path, Number(input.value)));
  return input;
}
function check(draft, path, label) {
  const box = h('input', { type: 'checkbox' });
  box.checked = !!getPath(draft, path);
  box.addEventListener('change', () => setPath(draft, path, box.checked));
  return h('label', { class: 'radio-line' }, box, h('span', {}, label));
}

export const TRUST_LEVELS = [
  ['ask', 'Ask', 'Every result waits for a person. The default, and the only level that has behaviour today.'],
  ['safe', 'Safe', 'Later: low-risk results that the gate passes may be queued for a quick look. Not active yet; the setting is stored.'],
  ['high', 'High', 'Later: only high-risk results need close review. Not active yet; the setting is stored.'],
];

export const STEPS = [
  {
    id: 1, title: 'Folder', purpose: 'Where the repository is, and the branch the agent starts from. Nothing is written to the repository.',
    build(draft, ctx) {
      const folder = text(draft, 'repositoryFolder', { mono: true, placeholder: 'C:\\code\\my-app  or  /home/me/my-app' });
      const key = text(draft, 'key', { mono: true });
      if (ctx.onFolder) folder.addEventListener('change', () => ctx.onFolder());
      if (ctx.mode !== 'new') { folder.disabled = true; key.disabled = true; }
      return h('div', { class: 'form-grid' },
        field('Repository folder', folder, ctx.mode === 'new' ? 'The top folder of the git repository (the one that contains .git).' : 'Fixed after the project is created.'),
        field('Base branch', text(draft, 'baseBranch', { mono: true }), 'Agent branches start here and drafts target it, for example main.'),
        field('Project key', key, ctx.mode === 'new' ? 'Short name: lower-case letters, digits, - and _. It cannot be changed later.' : 'Fixed after the project is created.'),
        field('Display name', text(draft, 'displayName')));
    },
  },
  {
    id: 2, title: 'Build and test', purpose: 'The commands that must pass on the base branch. The check runs them on a clean copy of that branch, exactly like the merge gate, so it can take a while.',
    build(draft) {
      return h('div', { class: 'form-grid' },
        field('Build command', text(draft, 'buildCommand', { mono: true }), 'For example: dotnet build'),
        field('Test command', text(draft, 'testCommand', { mono: true }), 'For example: dotnet test'));
    },
  },
  {
    id: 3, title: 'Paths', purpose: 'Where the agent may write, and which changes always need a close look. A change touching a risk path is never treated as low risk.',
    build(draft) {
      return h('div', { class: 'form-grid' },
        field('Writable paths', lines(draft, 'writablePaths'), 'One pattern per line, for example src/** and tests/**. The agent cannot write anywhere else.'),
        field('Risk paths', lines(draft, 'riskPaths', 7), 'Patterns for security, configuration and project files. The agent cannot write them either.'),
        field('Context documents', lines(draft, 'context', 3), 'Optional. Files or folders with project rules the agent reads on every run, for example docs/agent-rules.md. The agent cannot change them.'));
    },
  },
  {
    id: 4, title: 'Task source', purpose: 'Where the work comes from. Either write tasks here, or connect Azure DevOps. Secrets are never typed here: only the names of environment variables that hold them.',
    build(draft, ctx) {
      const source = h('div', {});
      const adoBox = h('div', { class: 'stack' });
      const preview = h('div', { class: 'stack' });
      const renderAdo = () => {
        clear(adoBox);
        if (draft.taskSource !== 'azure-devops') return;
        const auth = h('select', {}, [['pat', 'Personal access token (variable)'], ['az', 'Azure CLI sign-in'], ['service-principal', 'Service principal (three variables)']]
          .map(([v, l]) => h('option', { value: v, selected: draft.ado.auth === v }, l)));
        const authFields = h('div', { class: 'form-grid' });
        const drawAuth = () => {
          clear(authFields);
          if (draft.ado.auth === 'pat') authFields.append(field('Token variable name', text(draft, 'ado.tokenVariable', { mono: true }), 'The NAME of an environment variable that holds the token, starting with AUTOPILOT_. Set its value in your operating system.'));
          else if (draft.ado.auth === 'service-principal') authFields.append(
            field('Tenant id variable', text(draft, 'ado.tenantVariable', { mono: true })),
            field('Client id variable', text(draft, 'ado.clientIdVariable', { mono: true })),
            field('Client secret variable', text(draft, 'ado.clientSecretVariable', { mono: true }), 'Names only. The values stay in your environment.'));
          else authFields.append(h('p', { class: 'muted' }, 'Uses the account you are signed in with in the Azure CLI ("az login"). No variable needed.'));
        };
        auth.addEventListener('change', () => { draft.ado.auth = auth.value; drawAuth(); });
        drawAuth();
        const showPage = async (button, skip) => {
          await busy(button, async () => {
            clear(preview);
            preview.append(h('span', { class: 'spinner' }), ' Reading work items…');
            try {
              const page = await post('/wizard/azure-devops/preview', { draft, skip });
              clear(preview);
              if (!page.items.length) {
                preview.append(h('p', { class: 'banner' }, 'The connection works but the query returned no open items. Check the query, or tag an item "dev-autopilot".'));
                return;
              }
              const last = page.skip + page.items.length;
              preview.append(h('div', { class: 'stack' },
                h('p', {}, `Items ${page.skip + 1} to ${last} of ${page.total}${page.truncated ? ' (the query matches more; only the first are counted)' : ''}. Nothing was changed.`),
                h('ul', {}, page.items.map(i => h('li', {}, `#${i.id} ${i.title}`, h('span', { class: 'muted' }, ` (${[i.type, i.state].filter(Boolean).join(', ')})`)))),
                h('div', { class: 'row' },
                  page.skip > 0 ? h('button', { type: 'button', onclick: ev => showPage(ev.currentTarget, Math.max(0, page.skip - page.take)) }, 'Previous') : null,
                  page.hasMore ? h('button', { type: 'button', onclick: ev => showPage(ev.currentTarget, page.skip + page.take) }, 'Next') : null)));
            } catch (e) { clear(preview); preview.append(h('div', { class: 'banner bad' }, e.message)); }
          });
        };
        const previewBtn = h('button', { type: 'button', onclick: ev => showPage(ev.currentTarget, 0) }, 'Preview work items');
        adoBox.append(h('div', { class: 'form-grid' },
          field('Organization address', text(draft, 'ado.organizationUrl', { mono: true, placeholder: 'https://dev.azure.com/your-organization' })),
          field('Azure DevOps project', text(draft, 'ado.project')),
          field('Git repository name', text(draft, 'ado.repository'), 'Where draft pull requests are opened.'),
          field('Authentication', auth)),
          authFields,
          field('Query (optional)', text(draft, 'ado.query', { mono: true, placeholder: 'Leave empty: open items tagged dev-autopilot' }), 'A WIQL query. Empty uses the built-in one.'),
          h('div', {}, previewBtn), preview);
      };
      const radio = (value, label, hint) => {
        const input = h('input', { type: 'radio', name: 'task-source', value, checked: draft.taskSource === value });
        input.addEventListener('change', () => { draft.taskSource = value; renderAdo(); });
        return h('label', { class: 'radio-line' }, input, h('span', {}, h('b', {}, label), h('div', { class: 'hint' }, hint)));
      };
      source.append(radio('local', 'Local tasks', 'Write tasks and comments in this tool. Nothing is sent to any tracker.'),
        radio('azure-devops', 'Azure DevOps', 'Read work items from Azure DevOps and write back labels, comments and draft pull requests.'));
      renderAdo();
      return h('div', { class: 'stack' }, source, adoBox);
    },
  },
  {
    id: 5, title: 'Agent', purpose: 'Who the agent commits as, which models each role uses, and how much it may do before the gate looks at the result.',
    build(draft) {
      const role = (label, path) => h('fieldset', {}, h('legend', {}, label),
        h('div', { class: 'form-grid' },
          field('Model', text(draft, `${path}.model`, { mono: true, placeholder: 'empty: Claude Code default' }), 'A model name or alias such as sonnet or opus.'),
          field('Most tool calls', number(draft, `${path}.maxToolCalls`), 'After this many calls every further call is denied.')));
      return h('div', { class: 'stack' },
        h('fieldset', {}, h('legend', {}, 'Agent identity'), h('div', { class: 'form-grid' },
          field('Name', text(draft, 'identity.name'), 'The author name of the agent\'s commits.'),
          field('Email', text(draft, 'identity.email'), 'An address without spaces, for example autopilot@localhost.'))),
        role('Triage', 'triage'), role('Implementer', 'implementer'), role('Reviewer', 'reviewer'),
        h('fieldset', {}, h('legend', {}, 'Limits the gate enforces'), h('div', { class: 'form-grid' },
          field('Most files changed', number(draft, 'maxFilesChanged')),
          field('Most source lines changed', number(draft, 'maxSourceLinesChanged')),
          field('Most test lines changed', number(draft, 'maxTestLinesChanged')),
          check(draft, 'requireTestChange', 'Changed source code must come with a test change'))));
    },
  },
];

export const STEP_COUNT = STEPS.length + 1; // the summary is the last step

export function trustField(draft) {
  const box = h('fieldset', {}, h('legend', {}, 'Trust'));
  for (const [value, label, hint] of TRUST_LEVELS) {
    const input = h('input', { type: 'radio', name: 'trust', value, checked: draft.trust === value });
    input.addEventListener('change', () => { draft.trust = value; });
    box.append(h('label', { class: 'radio-line' }, input, h('span', {}, h('b', {}, label), h('div', { class: 'hint' }, hint))));
  }
  return box;
}

export function renderChecks(result) {
  const box = h('div', {});
  for (const c of result.checks) {
    box.append(h('div', { class: `check ${c.status}` },
      h('span', { class: 'name' }, c.name), h('span', {}, badge(c.status, c.status === 'ok' ? 'ok' : c.status === 'fail' ? 'bad' : c.status === 'warn' ? 'warn' : '')),
      c.detail ? h('div', { class: 'detail' }, c.detail.split('\n').map(l => l.trim()).filter(Boolean).join('\n')) : null,
      c.fix && c.status !== 'ok' ? h('div', { class: 'fix' }, h('b', {}, 'How to fix: '), c.fix) : null));
  }
  if (!result.checks.length) box.append(h('p', { class: 'muted' }, 'No checks for this step.'));
  return box;
}

/** Runs the checks of a step and shows them in `target`. Resolves to the result (or null on a transport error). */
export async function runChecks(step, draft, target, { projectKey } = {}) {
  clear(target);
  target.append(h('p', {}, h('span', { class: 'spinner' }), ' Checking… ', step === 2 || step === 6 ? h('span', { class: 'muted' }, 'The build and the tests run, this can take a few minutes.') : null));
  try {
    const result = projectKey
      ? await post(`/projects/${encodeURIComponent(projectKey)}/setup/check`, { step, draft })
      : await post('/wizard/check', { step, draft });
    clear(target);
    target.append(h('p', {}, result.passed ? badge('All checks passed', 'ok') : badge('Something needs fixing', 'bad')), renderChecks(result));
    return result;
  } catch (e) {
    clear(target);
    target.append(h('div', { class: 'banner bad' }, e.message));
    toast(e.message, true);
    return null;
  }
}

export async function loadDefaults(folder, baseBranch) {
  return post('/wizard/defaults', { folder, baseBranch });
}

/** Reads the folder (nothing is written) and returns { draft, analysis }: every value with the evidence behind it. */
export async function analyzeFolder(folder) {
  return post('/wizard/analyze', { folder });
}

const CONFIDENCE_KIND = { high: 'ok', medium: 'info', low: 'warn' };

function evidenceRow(label, d) {
  if (!d) return h('div', { class: 'evidence-row' }, h('span', { class: 'lbl' }, label), badge('not detected', 'warn'), h('div', { class: 'muted ev' }, 'Nothing in the folder says what this is. Fill it in below.'));
  const level = String(d.confidence).toLowerCase();
  return h('div', { class: 'evidence-row' },
    h('span', { class: 'lbl' }, label), h('code', {}, d.value),
    badge('detected', 'info'), badge(`${level} confidence`, CONFIDENCE_KIND[level] || ''),
    h('div', { class: 'muted ev' }, d.evidence),
    d.alternatives && d.alternatives.length ? h('div', { class: 'muted ev' }, `Other choices: ${d.alternatives.join('; ')}`) : null);
}

/** What the analyzer found for one part of the setup, with the file or rule behind each value. */
export function evidenceBox(analysis, group) {
  if (!analysis) return null;
  const rows = [];
  if (group === 'folder') {
    rows.push(evidenceRow('Base branch', analysis.baseBranch), evidenceRow('Project key', analysis.key), evidenceRow('Display name', analysis.displayName));
  } else if (group === 'commands') {
    rows.push(evidenceRow('Build', analysis.build), evidenceRow('Test', analysis.test));
  } else if (group === 'paths') {
    rows.push(...analysis.writablePaths.map(d => evidenceRow('Writable', d)), ...analysis.riskPaths.map(d => evidenceRow('Risk', d)), ...analysis.context.map(d => evidenceRow('Context', d)));
    if (!analysis.writablePaths.length) rows.unshift(evidenceRow('Writable', null));
  } else if (group === 'source') {
    rows.push(evidenceRow('Task source', analysis.taskSource));
    const ado = analysis.azureDevOps;
    if (ado) rows.push(evidenceRow('Organization', ado.organizationUrl), evidenceRow('Project', ado.project), evidenceRow('Repository', ado.repository));
  }
  return h('details', { class: 'evidence', open: rows.length <= 6 },
    h('summary', {}, `What was detected (${rows.length} value${rows.length === 1 ? '' : 's'}, with evidence)`), h('div', { class: 'stack' }, rows));
}

/** The values the analyzer cannot know, as a list the person works through. */
export function needsBox(analysis) {
  const notes = [...(analysis.needsInput || []).map(n => n.question), ...(analysis.warnings || [])];
  if (!notes.length) return h('p', {}, badge('Nothing to ask', 'ok'), ' Every value was found in the folder.');
  return h('div', { class: 'banner warn' },
    h('b', {}, (analysis.needsInput || []).length ? 'Please check these:' : 'Worth knowing:'),
    h('ul', {}, notes.map(n => h('li', {}, n))));
}

/** The few fields every project has: base branch, key, display name. The folder is fixed once analyzed. */
export function identityFields(draft) {
  const folder = text(draft, 'repositoryFolder', { mono: true });
  folder.disabled = true;
  return h('div', { class: 'form-grid' },
    field('Repository folder', folder, 'The repository that was analyzed. To use another folder, start again.'),
    field('Base branch', text(draft, 'baseBranch', { mono: true }), 'Agent branches start here and drafts target it.'),
    field('Project key', text(draft, 'key', { mono: true }), 'Short name: lower-case letters, digits, - and _. It cannot be changed later.'),
    field('Display name', text(draft, 'displayName')));
}

export { get };
