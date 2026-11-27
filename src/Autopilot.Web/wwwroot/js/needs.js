import { get, post } from './api.js';
import { h, clear, toast, badge, emptyState } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

export async function render({ view, key }) {
  const holder = h('div');
  view.append(holder);

  async function load() {
    const items = await get(`${base(key)}/needs-human`);
    clear(holder);
    holder.append(h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Needs a person'),
      h('p', {}, 'Triage could not decide these alone. Answer the question and the answer is stored on the task, so the next triage and the agent read it.'))));
    if (!items.length) { holder.append(h('div', { class: 'card' }, emptyState('When triage cannot decide a task alone, its question shows up here.', { label: 'Back to the pipeline', href: `#/p/${encodeURIComponent(key)}/pipeline` }, 'Nothing waits for you'))); return; }
    for (const item of items) {
      const answer = h('textarea', { rows: 4, placeholder: 'Your answer' });
      holder.append(h('section', { class: 'card stack' },
        h('div', { class: 'row between' }, h('h2', {}, `#${item.id} ${item.title}`), item.stale ? badge('text changed since triage', 'warn') : badge('needs-human', 'warn')),
        h('p', { class: 'prewrap' }, h('b', {}, 'Question: '), item.question),
        item.canAnswer
          ? [answer, h('div', { class: 'row' },
            h('button', { class: 'primary', type: 'button', onclick: async () => {
              try { await post(`${base(key)}/tasks/${encodeURIComponent(item.id)}/answer`, { text: answer.value }); toast('Answer stored on the task. Triage it again.'); await load(); }
              catch (e) { toast(e.message, true); }
            } }, 'Save answer'),
            h('a', { class: 'btn', href: `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(item.id)}` }, 'Open task'))]
          : h('p', { class: 'muted' }, 'This task lives in the tracker. Answer in a comment there.')));
    }
  }
  await load();
}
