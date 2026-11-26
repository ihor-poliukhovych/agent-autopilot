// "Import tasks" panel: a dry run first (counts only), then a confirmed import. Uses the saved settings of the project.
import { post } from './api.js';
import { h, clear, badge, busy } from './dom.js';

function counts(r) {
  return `${r.created} new, ${r.updated} updated, ${r.unchanged} unchanged`;
}

export function importPanel(key) {
  const out = h('div', { 'aria-live': 'polite' });
  const dry = h('button', { type: 'button' }, 'Preview import (nothing is written)');
  const confirm = h('button', { type: 'button', class: 'primary', hidden: true }, 'Import tasks');
  const fail = error => {
    const command = `autopilot ado check --project ${key}`;
    clear(out);
    out.append(h('div', { class: 'banner warn' },
      h('b', {}, 'The import did not run.'),
      ...(error.problems || [error.message]).map(m => h('p', {}, m)),
      h('p', {}, 'Run this in a terminal to see which check fails and how to fix it: ', h('code', {}, command), ' ',
        h('button', { type: 'button', onclick: () => navigator.clipboard && navigator.clipboard.writeText(command) }, 'Copy command'))));
  };
  const run = async dryRun => {
    try {
      const r = await post(`/projects/${encodeURIComponent(key)}/azure-devops/import`, { dryRun });
      clear(out);
      out.append(h('p', {}, badge(dryRun ? 'Preview' : 'Imported', 'ok'), ' ', counts(r), dryRun ? '. Nothing was written yet.' : '.'));
      confirm.hidden = !dryRun || r.created + r.updated === 0;
    } catch (e) { confirm.hidden = true; fail(e); }
  };
  dry.addEventListener('click', () => busy(dry, () => run(true)));
  confirm.addEventListener('click', () => busy(confirm, () => run(false)));
  return h('div', { class: 'stack' },
    h('h3', {}, 'Import tasks from Azure DevOps'),
    h('p', { class: 'muted small' }, 'Reads the work items of the saved query and copies them into this tool as tasks. Nothing is changed in Azure DevOps. Running it again only updates what changed.'),
    h('div', { class: 'row' }, dry, confirm), out);
}
