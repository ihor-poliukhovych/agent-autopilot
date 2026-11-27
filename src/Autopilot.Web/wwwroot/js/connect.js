// The single place that offers "Connect Azure DevOps?". The setup review page and the project settings page
// both call connectAzureDevOps(); a guided connect panel replaces the body of showConnected() and nothing else.
import { h, clear, badge } from './dom.js';
import { STEPS } from './setupform.js';

export function connectAzureDevOps(draft, { analysis = null, wizard = false } = {}) {
  const box = h('div', { class: 'stack' });
  const guess = analysis && analysis.azureDevOps;
  let declined = false;

  const showConnected = () => {
    box.append(
      h('p', {}, badge('Azure DevOps', 'ok'), ' Work items are read from Azure DevOps. Only names of environment variables are stored, never secrets.'),
      STEPS[3].build(draft, { mode: wizard ? 'new' : 'edit' }),
      h('div', { class: 'row' }, h('button', { type: 'button', onclick: () => { draft.taskSource = 'local'; declined = wizard; draw(); } }, 'Use local tasks instead')));
  };

  const showPrompt = () => {
    const where = guess ? `${guess.organizationUrl.value} / ${guess.project.value} / ${guess.repository.value}` : null;
    box.append(
      h('div', { class: 'banner info' },
        h('b', {}, 'Connect Azure DevOps?'),
        h('p', {}, where
          ? `This repository's remote is on Azure DevOps (${where}). Connect it to read work items from there and open draft pull requests.`
          : 'Tasks are local files you write in this tool. Connect Azure DevOps to read work items from there and open draft pull requests.'),
        declined ? h('p', {}, badge('Local tasks', 'ok'), ' You can connect Azure DevOps later from the project settings.') : null),
      h('div', { class: 'row' },
        h('button', { type: 'button', class: 'primary', onclick: () => { draft.taskSource = 'azure-devops'; declined = false; draw(); } }, 'Connect Azure DevOps'),
        wizard && !declined ? h('button', { type: 'button', onclick: () => { declined = true; draw(); } }, 'No, keep local tasks') : null));
  };

  const draw = () => { clear(box); if (draft.taskSource === 'azure-devops') showConnected(); else showPrompt(); };
  if (wizard && guess) draft.taskSource = 'local'; // ask first; the detected values stay in the draft
  draw();
  return box;
}
