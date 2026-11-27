// Feedback button and form. Builds a prefilled GitHub "new issue" page; the server redacts and shows what is sent.
import { post } from './api.js';
import { h, clear, field, toast } from './dom.js';

const KINDS = [['bug', 'Bug'], ['idea', 'Idea'], ['question', 'Question']];
let dialog = null;

function build() {
  const el = h('dialog', { id: 'feedback-dialog', 'aria-label': 'Send feedback' });
  document.body.append(el);
  return el;
}

function close() { if (dialog) dialog.close(); }

function open() {
  dialog ??= build();
  clear(dialog);
  const kind = h('select', { id: 'feedback-kind' }, KINDS.map(([v, t]) => h('option', { value: v }, t)));
  const title = h('input', { id: 'feedback-title', type: 'text', maxlength: '120', required: true, placeholder: 'Short summary' });
  const text = h('textarea', { id: 'feedback-text', rows: '6', placeholder: 'What happened, or what do you want?' });
  const context = h('input', { id: 'feedback-context', type: 'checkbox', checked: true });
  const page = location.hash || '#/';
  const preview = h('pre', { class: 'feedback-preview', hidden: true });
  const error = h('p', { class: 'feedback-error', role: 'alert', hidden: true });
  const send = h('button', { type: 'submit', class: 'primary' }, 'Preview and open GitHub');
  let prepared = null;

  const form = h('form', { class: 'stack', novalidate: true },
    h('h2', {}, 'Send feedback'),
    h('p', { class: 'hint' }, 'This opens a prefilled new issue on GitHub in your browser. Nothing is sent from here, and you can edit it there. Secrets, local paths and your user name are removed first.'),
    field('Type', kind),
    field('Title', title),
    field('Description', text),
    h('label', { class: 'row' }, context, h('span', {}, `Include the version, the operating system and this page (${page})`)),
    error, preview,
    h('div', { class: 'row end' }, h('button', { type: 'button', onclick: close }, 'Cancel'), send));

  form.addEventListener('submit', async e => {
    e.preventDefault();
    error.hidden = true;
    send.disabled = true;
    try {
      if (!prepared) {
        // First click: show exactly what will be sent.
        prepared = await post('/feedback', { kind: kind.value, title: title.value, description: text.value, includeContext: context.checked, page });
        preview.textContent = prepared.text;
        preview.hidden = false;
        send.textContent = 'Open GitHub';
        for (const control of [kind, title, text, context]) control.addEventListener('input', () => { prepared = null; preview.hidden = true; send.textContent = 'Preview and open GitHub'; }, { once: true });
      } else {
        window.open(prepared.url, '_blank', 'noopener');
        toast('GitHub opened in a new tab. Review the issue there and submit it.');
        close();
      }
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
    } finally { send.disabled = false; }
  });
  dialog.append(form);
  dialog.showModal();
  title.focus();
}

const button = h('button', { id: 'feedback-btn', class: 'feedback-btn', type: 'button', onclick: open, title: 'Report a bug or share an idea' }, 'Feedback');
document.getElementById('theme-toggle').before(button);
