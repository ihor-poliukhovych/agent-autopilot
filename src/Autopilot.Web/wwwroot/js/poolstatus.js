// What the Pool page says about a pool run, worked out from the pool status the API sends.
// Pure functions: no DOM, no network, no clock unless one is passed in. The page and the tests share them.

/** The five steps of the stepper, in order. */
export const STEPS = ['Planned', 'Running', 'Waiting for you', 'Final PR', 'Done'];

/** Rows the queue shows before "Show N more". */
export const QUEUE_CAP = 5;

/** Rows the event list shows before "All events". */
export const EVENT_CAP = 6;

/** A rough guess for the estimate: implement attempts plus a master call or a retry per task. */
export const AGENT_RUNS_PER_TASK = 2;

// Which step each backend run status sits on. Queued is still planning; the review of the whole run
// (verify, attack, judge, fix) is the Final PR step; a run that needs a person waits for you.
const STEP_OF = {
  Planned: 'Planned', Queued: 'Planned',
  Running: 'Running',
  NeedsPerson: 'Waiting for you',
  Reviewing: 'Final PR', ReadyForPerson: 'Final PR',
  Done: 'Done',
};

const SUB_TEXT = {
  Working: 'working',
  WaitingForSlot: 'waiting for a slot',
  PausedInfrastructure: 'paused: a tool or the network failed',
  PausedBudget: 'paused: usage limit reached',
  ParkedTasksWaiting: 'tasks are waiting for you',
  PrVerify: 'checking the final PR',
  Attack: 'attack review',
  Judge: 'judging',
  Fixing: 'fixing what was found',
  ReVerify: 'checking again',
};

const plural = (n, one, many) => `${n} ${n === 1 ? one : many ?? one + 's'}`;

/** The queue rows (status.queue) that wait for the person, counted by readiness word. */
export function readinessCounts(queue) {
  const counts = { ready: 0, waits: 0, notTriaged: 0, needsAnswer: 0, parked: 0 };
  for (const q of queue ?? []) {
    const word = q.readiness ?? '';
    if (word === 'ready') counts.ready++;
    else if (word.startsWith('waits for')) counts.waits++;
    else if (word === 'not triaged') counts.notTriaged++;
    else if (word === 'needs an answer') counts.needsAnswer++;
    else if (word === 'parked') counts.parked++;
  }
  return counts;
}

/** How many tasks wait for the person: parked ones, and the run itself when it needs a person. */
export function waitingCount(status) {
  const parked = status?.parked?.length ?? 0;
  return status?.status === 'NeedsPerson' ? Math.max(1, parked) : parked;
}

/** Tasks the agent works on right now and tasks that wait for a slot, from status.slots. */
export function slotCounts(status) {
  const slots = status?.slots ?? [];
  return { working: slots.filter(s => s.state === 'running').length, waiting: slots.filter(s => s.state !== 'running').length };
}

/**
 * The stepper: five steps, each done, current or to do. "Waiting for you" is amber and carries a count
 * only when something waits for the person. Failed and Cancelled mark no current step.
 */
export function stepper(status) {
  const name = status?.status ?? 'Planned';
  const current = STEP_OF[name] ?? null;
  const at = current ? STEPS.indexOf(current) : -1;
  const waiting = waitingCount(status);
  // A failed or cancelled run did at least get planned.
  const passed = current ? at : name === 'Failed' || name === 'Cancelled' ? 1 : 0;
  return STEPS.map((step, index) => {
    const isCurrent = step === current;
    const needs = step === 'Waiting for you' && waiting > 0;
    return {
      name: step,
      state: isCurrent ? (name === 'Done' ? 'done' : 'current') : needs ? 'need' : index < passed || name === 'Done' ? 'done' : 'todo',
      count: needs ? waiting : null,
    };
  });
}

/** The state pill next to the title. `busy` is true while a run holds the project. */
export function statePill(status, busy) {
  const name = status?.status ?? 'Planned';
  const live = name === 'Queued' || name === 'Running' || name === 'Reviewing';
  if (live && !busy) return { text: 'Not running', tone: 'idle' };
  if (name === 'Planned') return { text: 'Not started', tone: 'idle' };
  if (name === 'Queued') return { text: 'Queued', tone: 'run' };
  if (name === 'Running') return status?.pause ? { text: 'Paused', tone: 'idle' } : { text: 'Running', tone: 'run' };
  if (name === 'Reviewing') return { text: 'Final PR check', tone: 'run' };
  if (name === 'ReadyForPerson') return { text: 'Ready for you', tone: 'need' };
  if (name === 'NeedsPerson') return { text: 'Needs you', tone: 'need' };
  if (name === 'Done') return { text: 'Done', tone: 'ok' };
  if (name === 'Failed') return { text: 'Failed', tone: 'bad' };
  return { text: 'Cancelled', tone: 'idle' };
}

/** The small chips under the stepper: what the current step is doing right now. */
export function subChips(status) {
  const name = status?.status ?? 'Planned';
  const sub = status?.subStatus && status.subStatus !== 'None' ? status.subStatus : null;
  const counts = readinessCounts(status?.queue);
  const slots = slotCounts(status);
  const chips = [];
  const chip = (text, tone) => chips.push({ text, tone: tone ?? '' });
  let lead = STEP_OF[name] ?? name;
  if (name === 'Planned' || name === 'Queued') {
    const total = status?.queue?.length ?? 0;
    chip(plural(total, 'task') + ' ordered');
    if (name === 'Queued') chip('queued to start');
    if (counts.ready) chip(`${counts.ready} ready`);
    if (counts.waits) chip(`${counts.waits} wait${counts.waits === 1 ? 's' : ''} for another`);
    if (counts.notTriaged) chip(`${counts.notTriaged} not triaged`);
    if (counts.needsAnswer) chip(`${counts.needsAnswer} ${counts.needsAnswer === 1 ? 'needs' : 'need'} an answer`);
  } else if (name === 'Running') {
    if (sub === 'Working' || sub === null) chip(slots.working ? `${slots.working} working` : 'working', 'run');
    else chip(SUB_TEXT[sub] ?? sub);
    if (slots.waiting && sub === 'Working') chip(`${slots.waiting} waiting for a slot`);
    if (status?.pause && sub !== 'PausedInfrastructure' && sub !== 'PausedBudget') chip('paused');
  } else if (name === 'Reviewing') {
    chip(SUB_TEXT[sub] ?? 'reviewing', 'run');
  } else if (name === 'ReadyForPerson') {
    chip('the final PR is ready for you to review');
  } else if (name === 'NeedsPerson') {
    chip('a person has to decide', 'need');
  } else if (name === 'Done') {
    chip('finished');
  } else if (name === 'Failed' || name === 'Cancelled') {
    lead = name;
    chip(name === 'Failed' ? 'the run stopped with an error' : 'the run was cancelled');
  }
  return { lead, chips };
}

/** Segments of the progress strip: done, running, waiting for you, queued. They add up to the tasks counted. */
export function progressSegments(status) {
  const queue = status?.queue ?? [];
  const done = status?.progress?.done ?? 0;
  const counts = readinessCounts(queue);
  const running = Math.min(slotCounts(status).working, queue.length - counts.parked);
  const waiting = counts.parked;
  const queued = Math.max(0, queue.length - running - waiting);
  return { done, running, waiting, queued, total: done + queue.length };
}

/** When the latest run started (its first Start event), or null. */
export function startedAt(status) {
  const start = (status?.timeline ?? []).find(e => e.event === 'Start');
  return start ? new Date(start.time) : null;
}

/** About how many minutes are left, from the pace so far. Null until a task has finished. */
export function minutesLeft(status, now = new Date()) {
  const start = startedAt(status);
  const seg = progressSegments(status);
  if (!start || seg.done < 1) return null;
  const remaining = seg.running + seg.queued;
  if (remaining === 0) return null;
  const minutes = Math.round(((now - start) / 60000 / seg.done) * remaining);
  return minutes > 0 ? minutes : null;
}

export function duration(minutes) {
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`;
}

/** Rough count of agent runs a pool of this size needs. */
export function estimateAgentRuns(taskCount) {
  return taskCount * AGENT_RUNS_PER_TASK;
}

/** The one stat line under the strip: tasks done of the total, agent runs, and time left or the idle estimate. */
export function statLine(status, now = new Date()) {
  const seg = progressSegments(status);
  const max = status?.settings?.maxAgentRuns ?? null;
  const used = status?.agentRunsToday ?? 0;
  const active = status?.status && !['Planned', 'Queued'].includes(status.status);
  const left = minutesLeft(status, now);
  const tasks = [`${seg.done} of ${seg.total}`, ' done', seg.waiting ? ` · ${seg.waiting} waiting for you` : ''];
  let right;
  if (active || used > 0) {
    right = [max ? `${used} of ${max}` : `${used}`, ' agent runs', left ? ` · about ${duration(left)} left` : ''];
  } else {
    right = ['Estimate: about ', `${estimateAgentRuns(seg.total)}`, ' agent runs'];
  }
  return { tasks, right };
}

/** One word and a tone for a queue row. A task the agent works on shows its run state; the rest show readiness. */
export function rowPill(row, slot) {
  if (slot) return slot.state === 'running' ? { text: 'Running', tone: 'run' } : { text: 'Waiting for a slot', tone: 'idle' };
  const word = row?.readiness ?? '';
  if (word === 'ready') return { text: 'Ready', tone: 'ok' };
  if (word === 'needs an answer') return { text: 'Needs an answer', tone: 'need' };
  if (word === 'parked') return { text: 'Waiting for you', tone: 'need' };
  if (word === 'not triaged') return { text: 'Not triaged', tone: 'idle' };
  if (word.startsWith('waits for')) return { text: 'Waits for #' + word.slice('waits for'.length).trim(), tone: 'idle' };
  return { text: word || 'Unknown', tone: 'idle' };
}

/** The queue rows: all of them, or the first `cap` with the count that stays hidden. */
export function queueWindow(rows, expanded, cap = QUEUE_CAP) {
  const all = rows ?? [];
  if (expanded || all.length <= cap) return { visible: all, hidden: 0, canCollapse: expanded && all.length > cap };
  return { visible: all.slice(0, cap), hidden: all.length - cap, canCollapse: false };
}

const EVENT_TEXT = {
  Enqueue: () => ['The run was queued', 'run'],
  Start: () => ['The run started', 'run'],
  SlotUnavailable: () => ['Waiting for a free slot', 'idle'],
  SlotGranted: t => [`${t} started`, 'run'],
  PauseInfrastructure: () => ['Paused: a tool or the network failed', 'idle'],
  PauseBudget: () => ['Paused: the usage limit was reached', 'idle'],
  Resume: () => ['Resumed', 'run'],
  ParkTasks: () => ['Tasks are waiting for you', 'need'],
  ParkedTasksResolved: () => ['Waiting tasks were resolved', 'ok'],
  WorkFinished: () => ['All tasks were worked; checking the final PR', 'run'],
  VerifyPassed: () => ['Final PR checks passed', 'ok'],
  VerifyFailed: () => ['Final PR checks failed', 'bad'],
  AttackPassed: () => ['Attack review found nothing', 'ok'],
  AttackFoundIssues: () => ['Attack review found issues', 'bad'],
  JudgePassed: () => ['The judge approved', 'ok'],
  JudgeFoundIssues: () => ['The judge found issues', 'bad'],
  FixDone: () => ['Fixes are done', 'ok'],
  ReVerifyPassed: () => ['Checks passed again', 'ok'],
  ReVerifyFailed: () => ['Checks failed again', 'bad'],
  NeedPerson: () => ['The run needs you', 'need'],
  PersonAnswered: () => ['You answered', 'ok'],
  PersonApproved: () => ['You approved the run', 'ok'],
  PersonRejected: () => ['You sent the run back', 'run'],
  Fail: () => ['The run failed', 'bad'],
  Cancel: () => ['The run was cancelled', 'idle'],
  Retry: () => ['The run was retried', 'run'],
  TaskMerged: t => [`${t} merged into the integration branch`, 'ok'],
  TaskParked: t => [`${t} needs you`, 'need'],
  RunFinished: () => ['The run finished', 'ok'],
};

/** Text and dot tone of one timeline entry. `taskId` names the task when the event is about one. */
export function eventView(entry) {
  const task = entry?.taskId ? '#' + entry.taskId : 'A task';
  const [text, tone] = (EVENT_TEXT[entry?.event] ?? (() => [entry?.event ?? '', 'idle']))(task);
  const detail = entry?.detail && entry.detail !== 'merged' ? entry.detail : '';
  return { text: detail && (entry.event === 'TaskParked' || entry.event === 'TaskMerged' || entry.event === 'SlotGranted') ? `${text}: ${detail}` : text, tone };
}

/** The event list: newest first, capped unless `all`. Returns the rows and how many are left out. */
export function eventWindow(timeline, all, cap = EVENT_CAP) {
  const rows = [...(timeline ?? [])].reverse();
  return all || rows.length <= cap ? { rows, hidden: 0 } : { rows: rows.slice(0, cap), hidden: rows.length - cap };
}

/**
 * The settings a pool changed from the project default. The API sends `settings.defaults` only when the
 * project stores such defaults; without it nothing is marked.
 */
export function overrides(settings) {
  const defaults = settings?.defaults;
  if (!defaults) return {};
  const changed = {};
  for (const [key, value] of Object.entries(defaults)) {
    if (settings[key] !== undefined && settings[key] !== value) changed[key] = value;
  }
  return changed;
}
