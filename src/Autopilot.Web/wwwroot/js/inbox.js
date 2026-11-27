import { get, post } from './api.js';
import { h, clear, toast, badge, emptyState, ago } from './dom.js';
import * as needs from './needs.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

/** Keeps the count badge on the Inbox tab equal to the list; no badge when nothing waits. */
function setBadge(key, count) {
  const link = document.querySelector(`#tabs a[href="#/p/${encodeURIComponent(key)}/inbox"]`);
  if (!link) return;
  link.querySelector('.count')?.remove();
  if (count > 0) link.append(h('span', { class: 'count' }, String(count)));
}

// Everything that waits for a person, oldest first: needs-human questions and parked tasks.
export async function render({ view, key }) {
  let items = await get(`${base(key)}/inbox`);
  let selected = null;
  let stopNeeds = null;
  const list = h('div', { class: 'stack', 'data-part': 'inbox-list' });
  const detail = h('div', { class: 'stack', 'data-part': 'inbox-detail' });
  const same = (a, b) => a && b && a.kind === b.kind && a.id === b.id;

  async function reload() {
    items = await get(`${base(key)}/inbox`);
    if (selected && !items.some(i => same(i, selected))) selected = null;
    paintList();
    paintDetail();
  }

  function paintList() {
    setBadge(key, items.length);
    clear(list);
    if (!items.length) {
      list.append(emptyState('Questions for you and tasks the pool parked show up here.', null, 'Nothing waits for you'));
      return;
    }
    for (const item of items) {
      list.append(h('button', {
        type: 'button', class: 'card' + (same(item, selected) ? ' selected' : ''), 'data-kind': item.kind, 'data-id': item.id,
        'aria-current': same(item, selected) ? 'true' : null,
        onclick: () => { selected = item; paintList(); paintDetail(); },
      },
      h('div', { class: 'row between' }, h('b', {}, `#${item.id} ${item.title}`), item.kind === 'parked' ? badge('parked', 'warn') : badge('question', 'info')),
      h('p', { class: 'muted small' }, item.kind === 'parked' ? item.parkReason : (item.question?.question || ''), ' · ', ago(item.createdAt))));
    }
  }

  async function paintDetail() {
    if (stopNeeds) { stopNeeds(); stopNeeds = null; }
    clear(detail);
    if (!selected) {
      if (items.length) detail.append(h('p', { class: 'muted' }, 'Pick an item on the left.'));
      return;
    }
    if (selected.kind === 'parked') {
      const item = selected;
      detail.append(h('section', { class: 'card stack' },
        h('div', { class: 'row between' }, h('h2', {}, `#${item.id} ${item.title}`), badge('parked', 'warn')),
        h('p', { class: 'prewrap' }, item.parkReason),
        h('div', { class: 'row' },
          h('button', { type: 'button', class: 'primary', onclick: async () => {
            try { await post(`${base(key)}/pool/parked/${encodeURIComponent(item.id)}/unpark`); await reload(); } catch (e) { toast(e.message, true); }
          } }, 'Unpark'),
          h('a', { class: 'btn', href: `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(item.id)}` }, 'Open task'))));
      return;
    }
    // A question: the needs-a-person card with its option buttons, free-text field and Save and continue.
    const holder = h('div');
    detail.append(holder);
    const id = selected.id;
    stopNeeds = await needs.render({ view: holder, key, only: id, onChange: current => { if (!current.length) reload().catch(() => {}); } });
  }

  paintList();
  view.append(h('div', { class: 'app-layout', 'data-page': 'inbox' }, h('div', { class: 'app-left' }, list), h('div', { class: 'app-right open' }, detail)));
  paintDetail();
  return () => { if (stopNeeds) stopNeeds(); };
}
