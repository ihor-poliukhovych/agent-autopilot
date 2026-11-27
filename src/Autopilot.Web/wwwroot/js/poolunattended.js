import { get, post, put, del } from './api.js';
import { h, clear, toast, badge, field, ask, ago } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}/pool`;

const DAYS = [['mon', 'Mon'], ['tue', 'Tue'], ['wed', 'Wed'], ['thu', 'Thu'], ['fri', 'Fri'], ['sat', 'Sat'], ['sun', 'Sun']];

/**
 * The part of the Pool page for unattended runs: the concurrency slider, the unattended switch, who works on
 * what and why a task waits, the pool master's decisions, parked tasks, the integration branch, and the
 * scheduled run. `reload` redraws the whole page after a change.
 */
export async function render({ key, reload }) {
  const holder = h('div', { class: 'stack' });
  const [status, schedule] = await Promise.all([get(`${base(key)}/status`), get(`${base(key)}/schedule`)]);
  paint(holder, key, status, schedule, reload);
  return holder;
}

function paint(holder, key, status, schedule, reload) {
  clear(holder);
  const s = status.settings;
  const save = async change => {
    try { await put(`${base(key)}/settings`, change); await reload(); }
    catch (e) { toast(e.message, true); await reload(); }
  };

  holder.append(
    settingsCard(key, s, status, save, reload),
    slotsCard(status),
    decisionsCard(key, status, reload),
    parkedCard(key, status, reload),
    integrationCard(key, status, reload),
    scheduleCard(key, s, schedule, reload));
}

function settingsCard(key, s, status, save, reload) {
  const value = h('b', {}, String(s.concurrency));
  const slider = h('input', {
    type: 'range', min: 1, max: s.maxConcurrency, step: 1, value: s.concurrency, 'aria-label': 'Agents at once',
    oninput: () => { value.textContent = slider.value; },
    onchange: () => save({ concurrency: Number(slider.value) }),
  });
  const unattended = h('input', { type: 'checkbox', checked: s.unattended, onchange: () => save({ unattended: unattended.checked }) });
  const master = h('input', { type: 'checkbox', checked: s.masterEnabled, onchange: () => save({ masterEnabled: master.checked }) });
  const integration = h('input', { type: 'checkbox', checked: s.integrationEnabled, onchange: () => save({ integrationEnabled: integration.checked }) });
  return h('section', { class: 'card stack' },
    h('h2', {}, 'How the pool works'),
    status.pause ? h('div', { class: 'banner' },
      h('p', {}, h('b', {}, 'Paused. '), status.pause.reason, status.pause.retryAt ? ` It can start again after ${new Date(status.pause.retryAt).toLocaleString()}.` : ''),
      h('button', { type: 'button', onclick: async () => { try { await post(`${base(key)}/resume`); await reload(); } catch (e) { toast(e.message, true); } } }, 'Resume now')) : null,
    h('label', { class: 'field' }, h('span', { class: 'lbl' }, h('span', {}, 'Agents at once: '), value),
      slider,
      h('p', { class: 'hint' }, `1 to ${s.maxConcurrency}. A change applies to the next task the pool starts; agents that are working finish first. Tasks that write the same files, or wait for another task, still go one after the other.`)),
    h('label', { class: 'row' }, unattended, h('span', {}, h('b', {}, 'Run without a person. '),
      'The pool master answers questions and classifies failures. Questions about ' + s.alwaysAsk.join(', ') + ' are never answered by it: those tasks are parked for you.')),
    h('label', { class: 'row' }, master, h('span', {}, 'The pool master is on (it only acts when the pool runs without a person).')),
    h('label', { class: 'row' }, integration, h('span', {}, 'Task PRs go into an integration branch; the pool never merges into the base branch.')),
    h('p', { class: 'muted small' }, `Budget: ${s.maxAgentRuns ? s.maxAgentRuns + ' agent runs in 24 hours' : 'no limit on agent runs'}; stops at ${s.stopAtUsagePercent}% of the usage limit when that is known. Change it with "autopilot pool settings".`));
}

function slotsCard(status) {
  if (!status.slots.length) return null;
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Right now'),
    h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Task', 'State', 'Detail', 'Since'].map(t => h('th', {}, t)))),
      h('tbody', {}, status.slots.map(x => h('tr', {},
        h('td', { 'data-label': 'Task' }, `#${x.id} ${x.title}`),
        h('td', { 'data-label': 'State' }, badge(x.state === 'running' ? 'working' : 'waiting', x.state === 'running' ? 'ok' : 'warn')),
        h('td', { 'data-label': 'Detail' }, x.detail || ''),
        h('td', { 'data-label': 'Since' }, ago(x.since)))))))
  );
}

function decisionsCard(key, status, reload) {
  const list = status.decisions;
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Decisions of the pool master'),
    h('p', { class: 'muted' }, `${status.revertedDecisions} undone by a person so far. Undo takes the answer out of the task and puts the task back in the pool.`),
    !list.length ? h('p', { class: 'muted' }, 'The pool master has not decided anything yet.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Task', 'Kind', 'Answer', 'Why', ''].map(t => h('th', {}, t)))),
      h('tbody', {}, list.map(d => h('tr', {},
        h('td', { 'data-label': 'Task' }, `#${d.workItemId}`),
        h('td', { 'data-label': 'Kind' }, d.kind),
        h('td', { 'data-label': 'Answer' }, d.answer, d.revertedAt ? [' ', badge('undone', 'warn')] : null),
        h('td', { 'data-label': 'Why' }, d.reason),
        h('td', { class: 'actions' }, d.revertedAt ? null : h('button', { type: 'button', onclick: async () => {
          const ok = await ask({ title: 'Undo this decision?', confirm: 'Undo and rebuild', body: h('p', {}, `The answer is removed from task #${d.workItemId}, which goes back in the pool. Triage it again before the next run.`) });
          if (!ok) return;
          try { await post(`${base(key)}/decisions/${d.id}/revert`); toast('Undone.'); await reload(); } catch (e) { toast(e.message, true); }
        } }, 'Undo and rebuild'))))))));
}

function parkedCard(key, status, reload) {
  const list = status.parked;
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Parked for you'),
    !list.length ? h('p', { class: 'muted' }, 'Nothing is parked. Tasks end up here when the pool must not decide for you, or gave up.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Task', 'Topic', 'Why', 'Since', ''].map(t => h('th', {}, t)))),
      h('tbody', {}, list.map(p => h('tr', {},
        h('td', { 'data-label': 'Task' }, `#${p.workItemId}`),
        h('td', { 'data-label': 'Topic' }, p.category ? badge(p.category, 'warn') : ''),
        h('td', { 'data-label': 'Why' }, p.reason),
        h('td', { 'data-label': 'Since' }, ago(p.parkedAt)),
        h('td', { class: 'actions' }, h('button', { type: 'button', onclick: async () => {
          try { await post(`${base(key)}/parked/${encodeURIComponent(p.workItemId)}/unpark`); await reload(); } catch (e) { toast(e.message, true); }
        } }, 'Back to the pool'))))))));
}

function integrationCard(key, status, reload) {
  const i = status.integration;
  if (!i.enabled) {
    return h('section', { class: 'card stack' }, h('h2', {}, 'Integration branch'),
      h('p', { class: 'muted' }, `Switched off: each task PR goes straight to ${i.baseBranch}. A person merges every one.`));
  }
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Integration branch'),
    i.branch
      ? h('p', {}, h('code', {}, i.branch), ` collects the finished tasks, each merged after build and tests passed. You merge one final PR into ${i.baseBranch}.`)
      : h('p', { class: 'muted' }, `No branch yet. The next pool run creates one from ${i.baseBranch}.`),
    i.merges.length ? h('ul', {}, i.merges.map(m => h('li', {}, `#${m.workItemId} (${m.taskBranch}) ${ago(m.mergedAt)}`))) : null,
    i.finalPrUrl ? h('p', {}, 'Final PR (draft): ', h('a', { href: i.finalPrUrl, target: '_blank', rel: 'noopener noreferrer' }, i.finalPrUrl)) : null,
    i.branch && i.merges.length && !i.finalPrUrl ? h('button', { type: 'button', onclick: async () => {
      const ok = await ask({ title: 'Open the final PR?', confirm: 'Open as draft', body: [
        h('p', {}, `Pushes ${i.branch} and opens a draft PR into ${i.baseBranch} with the gh program.`),
        h('p', { class: 'muted small' }, 'It stays a draft. You mark it ready and merge it yourself.')] });
      if (!ok) return;
      try { const r = await post(`${base(key)}/integration/pr`); toast('Draft PR opened: ' + r.url); await reload(); } catch (e) { toast(e.message, true); }
    } }, 'Open the final PR (draft)…') : null);
}

function scheduleCard(key, s, schedule, reload) {
  const now = new Date();
  const pad = n => String(n).padStart(2, '0');
  const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  const date = h('input', { type: 'date', value: today, required: true });
  const time = h('input', { type: 'time', value: '02:00', required: true, style: 'width:8rem' });
  const repeat = h('select', {}, [['none', 'Once'], ['daily', 'Every day'], ['weekdays', 'Weekdays'], ['days', 'On chosen days']].map(([v, t]) => h('option', { value: v }, t)));
  const days = DAYS.map(([v, t]) => ({ v, box: h('input', { type: 'checkbox', value: v }), t }));
  const dayRow = h('div', { class: 'row', hidden: true }, days.map(d => h('label', { class: 'row' }, d.box, d.t)));
  repeat.addEventListener('change', () => { dayRow.hidden = repeat.value !== 'days'; });
  const until = h('input', { type: 'time', style: 'width:8rem' });
  const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', style: 'width:6rem' });
  const unattended = h('input', { type: 'checkbox', checked: s.unattended });
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;

  const body = () => {
    const chosen = days.filter(d => d.box.checked).map(d => d.v);
    const rep = repeat.value === 'days' ? chosen.join(',') : repeat.value;
    return {
      at: `${date.value} ${time.value}`,
      repeat: rep || null,
      until: until.value || null,
      limit: limit.value ? Number(limit.value) : null,
      unattended: unattended.checked,
    };
  };

  return h('section', { class: 'card stack' },
    h('h2', {}, 'Scheduled run'),
    scheduleSummary(key, schedule, reload),
    h('div', { class: 'row' }, field('Date', date), field(`Time (${zone})`, time), field('Repeat', repeat)),
    dayRow,
    h('div', { class: 'row' }, field('Stop starting tasks at', until, 'Optional.'), field('Most tasks', limit),
      h('label', { class: 'row' }, unattended, h('span', {}, 'Without a person'))),
    h('button', { type: 'button', onclick: async () => {
      try {
        const plan = await post(`${base(key)}/schedule/plan`, body());
        const ok = await ask({
          title: 'Install this scheduled run?', confirm: 'Install',
          body: [
            h('p', { class: 'prewrap small' }, plan.summary),
            h('p', { class: 'muted small' }, `Times are in ${plan.timeZone}. It runs on this computer, as your user. A run missed because the computer was off starts at the next sign-in while its stop time has not passed. Results are drafts; nothing is merged into the base branch.`),
          ],
        });
        if (!ok) return;
        const result = await post(`${base(key)}/schedule`, { ...body(), confirmed: true });
        toast('Scheduled.'); if (result.note) toast(result.note, true);
        await reload();
      } catch (e) { toast(e.message, true); }
    } }, 'Review and schedule…'));
}

function scheduleSummary(key, s, reload) {
  if (!s.set) return h('p', { class: 'muted' }, 'Nothing is scheduled.');
  const p = s.plan;
  return h('div', { class: 'banner info' },
    h('p', {}, `Runs ${p.when}${p.limit ? `, at most ${p.limit} tasks` : ''}${p.unattended ? ', without a person' : ''}${p.until ? `, stopping at ${p.until}` : ''}. `,
      s.next ? `Next: ${new Date(s.next).toLocaleString()}. ` : 'No later run. ',
      s.installedInOs ? badge('installed', 'ok') : badge('missing in the system scheduler: schedule it again', 'warn')),
    h('div', { class: 'row' },
      h('button', { type: 'button', class: 'danger', onclick: async () => {
        const ok = await ask({ title: 'Cancel the scheduled run?', confirm: 'Cancel it', danger: true, body: h('p', {}, 'The job is removed from the system scheduler.') });
        if (!ok) return;
        try { await del(`${base(key)}/schedule`); toast('Cancelled.'); await reload(); } catch (e) { toast(e.message, true); }
      } }, 'Cancel the run')));
}
