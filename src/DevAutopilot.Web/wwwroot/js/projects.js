import { h, badge, emptyState } from './dom.js';

export function render({ view, state, go }) {
  view.append(
    h('div', { class: 'page-head' },
      h('div', {}, h('h1', {}, 'Projects'),
        h('p', {}, `Workspace: ${state.workspace}`)),
      h('button', { class: 'primary', type: 'button', onclick: () => go('new') }, 'Add a project')));
  if (!state.projects.length) {
    view.append(h('div', { class: 'card' }, emptyState('No projects yet. Add one: the wizard checks each step before you go on.')));
    return;
  }
  view.append(h('div', { class: 'grid' }, state.projects.map(p => h('article', { class: 'card' },
    h('h2', {}, h('a', { href: `#/p/${encodeURIComponent(p.key)}/tasks` }, p.displayName)),
    h('p', { class: 'muted small' }, p.repositoryFolder),
    h('div', { class: 'row' },
      badge(p.taskSource === 'local' ? 'Local tasks' : 'Azure DevOps', 'info'),
      badge(`base: ${p.baseBranch}`),
      badge('merged by a person', 'ok')),
    h('div', { class: 'row', style: 'margin-top:.75rem' },
      h('a', { class: 'btn', href: `#/p/${encodeURIComponent(p.key)}/tasks` }, 'Tasks'),
      h('a', { class: 'btn', href: `#/p/${encodeURIComponent(p.key)}/runs` }, 'Runs'),
      h('a', { class: 'btn', href: `#/p/${encodeURIComponent(p.key)}/settings` }, 'Settings'))))));
}
