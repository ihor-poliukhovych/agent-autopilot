import { get, post, del } from './api.js';
import { h, clear, toast, badge, field, emptyState, busy, ask } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

export async function render({ view, key }) {
  const holder = h('div');
  view.append(holder);

  async function load() {
    const [items, schedule] = await Promise.all([get(`${base(key)}/pool`), get(`${base(key)}/pool/schedule`)]);
    clear(holder);
    const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', style: 'width:6rem' });
    const at = h('input', { type: 'time', value: '02:00', required: true, style: 'width:8rem' });
    const nightLimit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', style: 'width:6rem' });
    const number = input => input.value ? Number(input.value) : null;

    holder.append(
      h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, 'Pool'),
        h('p', {}, 'Tasks the agent works through one at a time, in this order. Every result is a draft that a person merges.'))),
      h('section', { class: 'card' },
        !items.length ? emptyState('The pool is empty. Add tasks from the Tasks page.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
          h('thead', {}, h('tr', {}, ['#', 'Task', 'Triage', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, items.map((p, index) => h('tr', {},
            h('td', { 'data-label': 'Order' }, String(p.position)),
            h('td', { 'data-label': 'Task' }, `#${p.id} ${p.title}`),
            h('td', { 'data-label': 'Triage' }, p.triage ? [badge(p.triage, p.triage === 'agent-ready' ? 'ok' : 'warn'), p.stale ? badge('stale', 'warn') : null] : badge('not triaged')),
            h('td', { class: 'actions' }, h('div', { class: 'row' },
              h('button', { type: 'button', 'aria-label': 'Move up', disabled: index === 0, onclick: () => move(key, p, items[index - 1].position, load) }, '↑'),
              h('button', { type: 'button', 'aria-label': 'Move down', disabled: index === items.length - 1, onclick: () => move(key, p, items[index + 1].position, load) }, '↓'),
              h('button', { type: 'button', class: 'danger', onclick: async () => { try { await del(`${base(key)}/pool/${encodeURIComponent(p.id)}`); await load(); } catch (e) { toast(e.message, true); } } }, 'Remove'))))))))),
      h('section', { class: 'card stack' },
        h('h2', {}, 'Run now'),
        h('p', { class: 'muted' }, 'Starts the pool in the background. Only one run can be active in a project at a time.'),
        h('div', { class: 'row' }, field('Most items to try', limit),
          h('button', { class: 'primary', type: 'button', disabled: !items.length, onclick: async ev => {
            await busy(ev.currentTarget, async () => {
              try { await post(`${base(key)}/pool/run`, { limit: number(limit) }); toast('Pool run started. Follow it on the Runs page.'); }
              catch (e) { toast(e.message, true); }
            });
          } }, 'Run now'))),
      h('section', { class: 'card stack' },
        h('h2', {}, 'Run at night'),
        scheduleSummary(schedule),
        h('div', { class: 'row' }, field('Time (24 hours, this computer)', at), field('Most items per night', nightLimit),
          h('button', { type: 'button', onclick: async () => {
            try {
              const plan = await post(`${base(key)}/pool/schedule/plan`, { at: at.value, limit: number(nightLimit) });
              const ok = await ask({
                title: 'Install the night run?',
                confirm: 'Install',
                body: [
                  h('p', { class: 'prewrap small' }, plan.summary),
                  h('p', {}, h('b', {}, 'Tasks that would run:')),
                  plan.items.length ? h('ol', {}, plan.items.map(i => h('li', {}, `#${i.id} ${i.title}`))) : h('p', { class: 'muted' }, 'The pool is empty now. Tasks you add later run too.'),
                  h('p', { class: 'muted small' }, 'It runs on this computer, as your user, and needs it switched on at that time. Each result is a draft; nothing is merged.'),
                ],
              });
              if (!ok) return;
              await post(`${base(key)}/pool/schedule`, { at: at.value, limit: number(nightLimit), confirmed: true });
              toast('The night run is installed.');
              await load();
            } catch (e) { toast(e.message, true); }
          } }, 'Review and install…'))));
  }

  function scheduleSummary(s) {
    if (!s.set) return h('p', { class: 'muted' }, 'No night run is set.');
    return h('div', { class: 'banner info' },
      h('p', {}, `Set for ${s.plan.atTime} every day${s.plan.limit ? `, at most ${s.plan.limit} items` : ''}. `,
        s.installedInOs ? badge('installed', 'ok') : badge('missing in the system scheduler: install it again', 'warn')),
      h('button', { type: 'button', class: 'danger', onclick: async () => { try { await del(`${base(key)}/pool/schedule`); toast('Removed.'); await load(); } catch (e) { toast(e.message, true); } } }, 'Remove the night run'));
  }

  await load();
}

async function move(key, item, position, reload) {
  try { await post(`${base(key)}/pool/${encodeURIComponent(item.id)}/move`, { position }); await reload(); }
  catch (e) { toast(e.message, true); }
}
