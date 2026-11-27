import { h } from './dom.js';

// A placeholder: the Inbox content comes later. It only links to pages that already exist.
export async function render({ view, key }) {
  const k = encodeURIComponent(key);
  view.append(h('section', { class: 'card stack', 'data-page': 'inbox' },
    h('h1', {}, 'Inbox'),
    h('p', { class: 'muted' }, 'The Inbox content comes later. Until then, use these pages:'),
    h('ul', {},
      h('li', {}, h('a', { href: `#/p/${k}/needs` }, 'Needs a person')),
      h('li', {}, h('a', { href: `#/p/${k}/runs` }, 'Runs')))));
}
