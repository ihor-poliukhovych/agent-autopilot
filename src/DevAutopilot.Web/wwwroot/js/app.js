import { get, ApiError } from './api.js';
import { h, clear, toast } from './dom.js';
import * as projects from './projects.js';
import * as tasks from './tasks.js';
import * as pool from './pool.js';
import * as runs from './runs.js';
import * as needs from './needs.js';
import * as settings from './settings.js';
import * as wizard from './wizard.js';

const TABS = [
  ['tasks', 'Tasks', tasks], ['pool', 'Pool', pool], ['runs', 'Runs', runs],
  ['needs', 'Needs a person', needs], ['settings', 'Settings', settings],
];
const view = document.getElementById('view');
const tabs = document.getElementById('tabs');
const switcher = document.getElementById('project-switch');
let state = { projects: [] };
let cleanup = null;
let lastTab = 'tasks';

// ---- theme ----
function storedTheme() { try { return localStorage.getItem('autopilot-theme'); } catch { return null; } }
function applyTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme;
}
applyTheme(storedTheme());
document.getElementById('theme-toggle').addEventListener('click', () => {
  const dark = document.documentElement.dataset.theme
    ? document.documentElement.dataset.theme === 'dark'
    : matchMedia('(prefers-color-scheme: dark)').matches;
  const next = dark ? 'light' : 'dark';
  applyTheme(next);
  try { localStorage.setItem('autopilot-theme', next); } catch { /* the choice just is not remembered */ }
});

// ---- routing ----
function parse() {
  const parts = location.hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent);
  if (parts[0] === 'p' && parts[1]) return { name: 'project', key: parts[1], tab: parts[2] || 'tasks', rest: parts.slice(3) };
  if (parts[0] === 'new') return { name: 'new' };
  return { name: 'projects' };
}

export function go(path) { location.hash = '#/' + path; }

export async function refreshState() {
  state = await get('/state');
  clear(switcher);
  switcher.append(h('option', { value: '' }, state.projects.length ? 'Choose a project' : 'No projects yet'));
  for (const p of state.projects) switcher.append(h('option', { value: p.key }, p.displayName));
  switcher.append(h('option', { value: '+new' }, '+ Add a project'));
  return state;
}

switcher.addEventListener('change', () => {
  if (switcher.value === '+new') go('new');
  else if (switcher.value) go(`p/${encodeURIComponent(switcher.value)}/${lastTab}`);
  else go('projects');
});

let generation = 0;

async function route() {
  const mine = ++generation;
  if (cleanup) { try { cleanup(); } catch { /* ignore */ } cleanup = null; }
  const r = parse();
  // Each route draws into its own stage; only the newest route may put its stage on the page.
  const stage = h('div');
  const tabBar = h('div', { class: 'tabs-inner', style: 'display:contents' });
  let stop = null;
  if (!view.firstChild) view.append(h('p', { class: 'muted' }, 'Loading…'));
  document.title = 'Dev Autopilot';
  try {
    if (r.name === 'project') {
      if (!state.projects.some(p => p.key === r.key)) await refreshState();
      const project = state.projects.find(p => p.key === r.key);
      if (!project) {
        stage.append(h('div', { class: 'banner bad' }, `There is no project "${r.key}".`));
      } else {
        lastTab = r.tab === 'run' ? 'runs' : (TABS.some(t => t[0] === r.tab) ? r.tab : 'tasks');
        renderTabs(tabBar, r);
        const screen = (TABS.find(t => t[0] === lastTab) || TABS[0])[2];
        document.title = `${project.displayName} · Dev Autopilot`;
        stop = await screen.render({ view: stage, project, key: r.key, rest: r.rest, state, go, refreshState }) || null;
      }
    } else if (r.name === 'new') {
      stop = await wizard.render({ view: stage, mode: 'new', state, go, refreshState }) || null;
    } else {
      await refreshState();
      stop = await projects.render({ view: stage, state, go }) || null;
    }
  } catch (e) {
    stage.append(h('div', { class: 'banner bad' }, e instanceof ApiError && e.status === 401
      ? 'You are signed out. Start "autopilot ui" again and open the new link it prints.'
      : (e.message || 'Something went wrong.')));
  }
  if (mine !== generation) { if (stop) stop(); return; }
  cleanup = stop;
  switcher.value = r.name === 'project' ? r.key : '';
  clear(tabs).append(tabBar);
  view.replaceChildren(stage);
  view.focus({ preventScroll: true });
}

function renderTabs(bar, r) {
  for (const [id, label] of TABS) {
    const link = h('a', { href: `#/p/${encodeURIComponent(r.key)}/${id}` }, label);
    if (id === lastTab) link.setAttribute('aria-current', 'page');
    bar.append(link);
    if (id === 'needs') {
      get(`/projects/${encodeURIComponent(r.key)}/needs-human`).then(items => {
        if (items.length) link.append(h('span', { class: 'count' }, String(items.length)));
      }).catch(() => { /* the badge is optional */ });
    }
  }
}

addEventListener('hashchange', route);
try {
  await refreshState();
} catch (e) {
  toast(e.message, true);
}
route();
