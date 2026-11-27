import { get, post } from './api.js';
import { h, clear, put, toast, badge, emptyState } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;
const letter = index => String.fromCharCode(65 + index);

// Drafts live in memory and in localStorage, per task, so a re-render or a reload never loses typed text.
const drafts = new Map();
const draftKey = (key, id) => `autopilot-draft:${key}:${id}`;
function readDraft(key, id) {
  if (drafts.has(draftKey(key, id))) return drafts.get(draftKey(key, id));
  try { return localStorage.getItem(draftKey(key, id)) || ''; } catch { return ''; }
}
function writeDraft(key, id, text) {
  drafts.set(draftKey(key, id), text);
  try { if (text) localStorage.setItem(draftKey(key, id), text); else localStorage.removeItem(draftKey(key, id)); } catch { /* the draft stays in memory only */ }
}

const CONTINUE_KEY = 'autopilot-needs-continue';
function readContinue() { try { return localStorage.getItem(CONTINUE_KEY) !== 'off'; } catch { return true; } }
function writeContinue(on) { try { localStorage.setItem(CONTINUE_KEY, on ? 'on' : 'off'); } catch { /* not remembered */ } }

/** The text stored on the task for a picked option, with the free-text note after it. */
export function answerText(index, option, note) {
  const text = index === null ? '' : `Option ${letter(index)}: ${option}`;
  const extra = (note || '').trim();
  return [text, extra].filter(Boolean).join('\n\n');
}

const wait = ms => new Promise(r => setTimeout(r, ms));

/** Starts a triage and waits for its job. Only one triage runs at a time, so a busy answer waits and tries again. */
async function triageAndWait(key, id, stopped, onState) {
  let job = null;
  for (let tries = 0; !job && tries < 150; tries++) {
    if (stopped()) return null;
    try { job = await post(`${base(key)}/tasks/${encodeURIComponent(id)}/triage`); }
    catch (e) {
      if (e.status !== 409) throw e;
      onState('Waiting for another triage to end…');
      await wait(3000);
    }
  }
  if (!job) throw new Error('Another triage kept running. Triage this task again in a moment.');
  onState('Triage running…');
  while (job.isActive && !stopped()) {
    await wait(2000);
    job = (await get(`${base(key)}/jobs/${encodeURIComponent(job.id)}`)).job;
  }
  return job;
}

export async function render({ view, key }) {
  const holder = h('div');
  const list = h('div', { class: 'stack' });
  const top = h('div');
  view.append(holder);
  let stopped = false;
  const cards = new Map();
  let batchRunning = false;
  const continueBox = h('input', { type: 'checkbox', checked: readContinue(), id: 'needs-continue', onchange: () => writeContinue(continueBox.checked) });

  async function fetchItems() { return get(`${base(key)}/needs-human`); }

  function header(items) {
    const answered = items.filter(i => i.canAnswer && i.answer && i.stale);
    clear(top).append(
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', {}, 'Needs a person'),
          h('p', {}, 'Triage could not decide these alone. Pick an answer or write one: it is stored on the task, so the next triage and the agent read it.')),
        h('div', { class: 'row' },
          h('button', { type: 'button', onclick: () => refresh().catch(e => toast(e.message, true)) }, 'Refresh'),
          h('button', { type: 'button', class: 'primary', disabled: !answered.length || batchRunning, title: 'Triage every task that has an answer and put it in the pool', onclick: ev => triageAll(ev.currentTarget, answered) },
            answered.length ? `Triage all answered (${answered.length})` : 'Triage all answered'))),
      h('label', { class: 'check-row' }, continueBox, h('span', {}, 'After an answer is saved, triage the task again and put it in the pool.')));
  }

  async function triageAll(button, answered) {
    batchRunning = true;
    button.disabled = true;
    let done = 0;
    for (const item of answered) {
      if (stopped) break;
      button.textContent = `Triaging ${done + 1} of ${answered.length}…`;
      const card = cards.get(item.id);
      try {
        await post(`${base(key)}/pool`, { id: item.id });
        const job = await triageAndWait(key, item.id, () => stopped, text => card?.status(text));
        if (job && job.status !== 'succeeded') toast(`Triage of #${item.id} failed: ${job.message || 'see the job log'}`, true);
      } catch (e) { toast(e.message, true); }
      done++;
      await refreshCard(item.id);
    }
    batchRunning = false;
    if (stopped) return;
    toast(`Triaged ${done} task${done === 1 ? '' : 's'}.`);
    await refresh();
  }

  /** Redraws one card from the server's current data; the other cards, and what is typed in them, stay. */
  async function refreshCard(id) {
    const items = await fetchItems();
    const item = items.find(i => i.id === id);
    const card = cards.get(id);
    if (!card) return;
    if (!item) { card.finished(); } else card.update(item);
    header(items);
  }

  async function refresh() {
    const items = await fetchItems();
    header(items);
    const seen = new Set();
    for (const item of items) {
      seen.add(item.id);
      const card = cards.get(item.id);
      if (card) card.update(item); else addCard(item);
    }
    for (const [id, card] of cards) if (!seen.has(id)) { card.element.remove(); cards.delete(id); }
    empty(items);
  }

  function empty(items) {
    if (items.length) return;
    clear(list).append(h('div', { class: 'card' }, emptyState('When triage cannot decide a task alone, its question shows up here.', { label: 'Back to the pipeline', href: `#/p/${encodeURIComponent(key)}/pipeline` }, 'Nothing waits for you')));
    cards.clear();
  }

  function addCard(item) {
    const card = makeCard(item);
    cards.set(item.id, card);
    list.append(card.element);
  }

  function makeCard(first) {
    let item = first;
    const element = h('section', { class: 'card stack need' });
    const note = h('textarea', { rows: 3, placeholder: 'Or write your own answer, or add a note to the option you pick', 'aria-label': 'Your answer or note', oninput: () => writeDraft(key, item.id, note.value) });
    note.value = readDraft(key, item.id);
    const statusLine = h('p', { class: 'muted small', role: 'status' });
    let working = false;

    const status = text => { statusLine.textContent = text || ''; };

    async function save(index) {
      if (working) return;
      const text = answerText(index, index === null ? '' : item.options[index], note.value);
      if (!text) { toast('Pick an option or write an answer first.', true); return; }
      working = true; paintButtons();
      try {
        const path = `${base(key)}/tasks/${encodeURIComponent(item.id)}/`;
        if (continueBox.checked) {
          const result = await post(path + 'answer-and-continue', { text });
          writeDraft(key, item.id, ''); note.value = '';
          toast('Answer saved. The task is in the pool.');
          if (result.note) toast(result.note, true);
          await refreshCard(item.id);
          working = false; paintButtons();
          if (result.triageJob?.isActive) await followTriage(result.triageJob);
          else if (result.note) await triageLater();
          return;
        }
        await post(path + 'answer', { text });
        writeDraft(key, item.id, ''); note.value = '';
        toast('Answer saved. Triage the task again to check it.');
        await refreshCard(item.id);
      } catch (e) { toast(e.message, true); }
      working = false; paintButtons();
    }

    async function followTriage(job) {
      status('Triage running…');
      try {
        while (job.isActive && !stopped) { await wait(2000); job = (await get(`${base(key)}/jobs/${encodeURIComponent(job.id)}`)).job; }
        if (job.status === 'succeeded') toast(`Triage of #${item.id} done.`);
        else toast(`Triage of #${item.id} failed: ${job.message || 'see the job log'}`, true);
      } catch (e) { toast(e.message, true); }
      status('');
      if (!stopped) await refreshCard(item.id);
    }

    async function triageLater() {
      try {
        const job = await triageAndWait(key, item.id, () => stopped, status);
        if (job && job.status !== 'succeeded') toast(`Triage of #${item.id} failed: ${job.message || 'see the job log'}`, true);
      } catch (e) { toast(e.message, true); }
      status('');
      if (!stopped) await refreshCard(item.id);
    }

    const buttonsHost = h('div', { class: 'stack' });
    function paintButtons() {
      clear(buttonsHost);
      if (!item.canAnswer) return;
      const options = item.options || [];
      if (options.length) {
        // The recommended option goes first and is highlighted; letters keep the order triage gave.
        const order = options.map((text, index) => ({ text, index }));
        order.sort((a, b) => (b.index === item.recommended) - (a.index === item.recommended));
        buttonsHost.append(h('div', { class: 'options', role: 'group', 'aria-label': 'Answers' }, order.map(o => {
          const rec = o.index === item.recommended;
          return h('button', { type: 'button', class: 'option' + (rec ? ' recommended' : ''), disabled: working, 'data-option': letter(o.index), onclick: () => save(o.index) },
            h('span', { class: 'key', 'aria-hidden': 'true' }, letter(o.index)),
            h('span', { class: 'text' }, o.text),
            rec ? h('span', { class: 'badge info' }, 'Recommended') : null);
        })));
      }
      put(buttonsHost, note,
        h('div', { class: 'row' },
          h('button', { type: 'button', class: options.length ? '' : 'primary', disabled: working, onclick: () => save(null) }, continueBox.checked ? 'Save and continue' : 'Save answer'),
          item.stale && !continueBox.checked ? h('button', { type: 'button', disabled: working, onclick: async () => { working = true; paintButtons(); await triageLater(); working = false; paintButtons(); } }, 'Triage again') : null,
          h('a', { class: 'btn', href: `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(item.id)}` }, 'Open task')));
    }

    function paint() {
      const answerNode = item.answer ? h('div', { class: 'comment' }, h('div', { class: 'who' }, 'Your answer'), h('div', { class: 'body prewrap' }, item.answer)) : null;
      put(clear(element),
        h('div', { class: 'row between' }, h('h2', {}, `#${item.id} ${item.title}`),
          h('div', { class: 'row' }, item.inPool ? badge('in the pool', 'info') : null, item.stale ? badge(item.answer ? 'answered, triage again' : 'text changed since triage', 'warn') : badge('needs-human', 'warn'))),
        h('p', { class: 'prewrap question' }, item.question),
        answerNode,
        statusLine,
        item.canAnswer ? buttonsHost : h('p', { class: 'muted' }, 'This task lives in the tracker. Answer in a comment there.'));
      paintButtons();
    }
    paint();

    continueBox.addEventListener('change', paintButtons);

    return {
      element,
      status,
      update(next) { item = next; paint(); },
      finished() {
        put(clear(element), h('div', { class: 'row between' }, h('h2', {}, `#${item.id} ${item.title}`), badge('triaged', 'ok')),
          h('p', { class: 'muted' }, 'No longer waiting for a person.'));
      },
    };
  }

  const items = await fetchItems();
  holder.append(top, list);
  header(items);
  if (items.length) for (const item of items) addCard(item); else empty(items);
  return () => { stopped = true; };
}
