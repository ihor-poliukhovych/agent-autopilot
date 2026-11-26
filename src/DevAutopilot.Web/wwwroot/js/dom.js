// Tiny helpers. Text is always set as text, never as HTML.
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (key === 'style') el.style.cssText = value; // set through the CSSOM: the content policy blocks style attributes
    else if (key === 'value') el.value = value;
    else if (key === 'checked' || key === 'disabled' || key === 'selected' || key === 'open') el[key] = !!value;
    else el.setAttribute(key, value === true ? '' : String(value));
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

/** Like el.append, but skips null, undefined and false (native append would print "null"). */
export function put(el, ...children) { return append(el, children); }

export function clear(el) { el.replaceChildren(); return el; }

export function toast(message, bad = false) {
  const box = document.getElementById('toasts');
  const item = h('div', { class: 'toast' + (bad ? ' bad' : '') }, message);
  box.append(item);
  setTimeout(() => item.remove(), bad ? 8000 : 3500);
}

export function ask({ title, body, confirm = 'Confirm', danger = false, cancel = 'Cancel' }) {
  const dialog = document.getElementById('dialog');
  return new Promise(resolve => {
    const done = value => { dialog.close(); resolve(value); };
    clear(dialog);
    dialog.append(
      h('h2', {}, title), h('div', { class: 'stack' }, body),
      h('div', { class: 'row end' },
        h('button', { type: 'button', onclick: () => done(false) }, cancel),
        h('button', { type: 'button', class: danger ? 'danger' : 'primary', onclick: () => done(true) }, confirm)));
    dialog.addEventListener('cancel', () => resolve(false), { once: true });
    dialog.showModal();
  });
}

export function ago(iso) {
  if (!iso) return '';
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'just now';
  for (const [size, name] of [[86400, 'd'], [3600, 'h'], [60, 'min']]) {
    if (seconds >= size) return `${Math.floor(seconds / size)} ${name} ago`;
  }
  return '';
}

export function badge(text, kind = '') { return h('span', { class: 'badge ' + kind }, text); }

export function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', { class: 'lbl' }, label), input, hint ? h('p', { class: 'hint' }, hint) : null);
}

export function emptyState(text) { return h('div', { class: 'empty' }, text); }

export async function busy(button, work) {
  button.disabled = true;
  try { return await work(); } finally { button.disabled = false; }
}
