import { createSync, type Pull, type Remote } from '../sync/engine';
import { createMemoryStorage, type OutboxItem } from '../sync/storage';
import { createOfflineToday } from '../today/offline';
import type { ScheduledActionView } from '../today/today';
import { MOMENTS_AHEAD, groupByMoment, reminderTime, toArm, upcomingReminders } from './arming';
import { handleReminderResponse, parseMinutes, parseReminderData, type ReminderResponse } from './headless';
import { upcomingFollowUps } from './followUp';
import { en } from '../i18n/en';
import { arm, type ArmedReminder, type Notifier } from './notifier';
import { askForReminderPermissions, askOnceAfterFirstPlan, type PermissionPlatform, type PermissionStep } from './permissions';

const action = (over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id: 'a1', aspirationId: 'asp', date: '2026-01-07', slotId: 's1', slot: 'weekday mornings', slotStart: '07:00', slotEnd: '07:45',
  title: 'Easy run', minutes: 30, checkIn: null, reminderKind: 'now', reminderLeadMinutes: null, reminderText: 'Shoes by the door.', reminderOn: true,
  ...over,
});
const at = (h: number, m = 0, day = 7) => new Date(2026, 0, day, h, m);
const NIGHT_BEFORE = at(20, 0, 6);

describe('which reminders are due', () => {
  it('gives one reminder per Action, at the Slot start for "now"', () => {
    const r = upcomingReminders([action(), action({ id: 'a2', date: '2026-01-09' })], NIGHT_BEFORE);
    expect(r.map((x) => [x.actionId, x.at])).toEqual([['a1', at(7)], ['a2', at(7, 0, 9)]]);
  });

  it('rings a get-ready Action once, a lead ahead, and never again at the start', () => {
    const a = action({ reminderKind: 'get_ready', reminderLeadMinutes: 90 });
    expect(reminderTime(a)).toEqual(at(5, 30));
    expect(upcomingReminders([a], NIGHT_BEFORE).map((x) => x.at)).toEqual([at(5, 30)]);
    // Once the lead time has passed, the start is not a second chance.
    expect(upcomingReminders([a], at(5, 45))).toEqual([]);
  });

  it('defaults a get-ready lead to an hour', () => {
    expect(reminderTime(action({ reminderKind: 'get_ready', reminderLeadMinutes: null }))).toEqual(at(6, 0));
  });

  it('skips checked-in, switched-off and past Actions', () => {
    const checkIn = { state: 'done' as const, minutes: null, reasons: [], note: null, tappedAt: '2026-01-06T00:00:00Z' };
    const r = upcomingReminders(
      [action({ id: 'done', checkIn }), action({ id: 'off', reminderOn: false }), action({ id: 'past', date: '2026-01-05' }), action({ id: 'ok' })],
      NIGHT_BEFORE,
    );
    expect(r.map((x) => x.actionId)).toEqual(['ok']);
  });

  it('shares one group between reminders due at the same moment, one child per Action', () => {
    const groups = groupByMoment(upcomingReminders([action({ id: 'b' }), action({ id: 'a' }), action({ id: 'later', slotStart: '12:00' })], NIGHT_BEFORE));
    expect(groups.map((g) => g.reminders.map((r) => r.actionId))).toEqual([['a', 'b'], ['later']]);
    expect(groups[0]!.key).not.toBe(groups[1]!.key);
  });

  it('groups a get-ready reminder with a "now" one that falls on the same minute', () => {
    const groups = groupByMoment(
      upcomingReminders([action({ id: 'now', slotStart: '06:00' }), action({ id: 'ready', reminderKind: 'get_ready', reminderLeadMinutes: 60 })], NIGHT_BEFORE),
    );
    expect(groups).toHaveLength(1);
    expect(groups[0]!.reminders).toHaveLength(2);
  });
});

describe('arming', () => {
  function fakeNotifier() {
    let armed: ArmedReminder[] = [];
    const dismissed: string[] = [];
    const n: Notifier = {
      async cancelAll() { armed = []; },
      async schedule(r) { armed.push(r); },
      async dismiss(id) { dismissed.push(id); },
    };
    return { n, armed: () => armed, dismissed };
  }
  const week = ['2026-01-07', '2026-01-08', '2026-01-09', '2026-01-10', '2026-01-11'].map((date, i) => action({ id: `d${i}`, date }));

  it('arms a wide window, so a dead app still rings the reminders after the first few', async () => {
    const f = fakeNotifier();
    await arm(f.n, toArm(week, NIGHT_BEFORE));
    expect(MOMENTS_AHEAD).toBeGreaterThan(week.length);
    expect(f.armed().map((r) => r.data.actionId)).toEqual(['d0', 'd1', 'd2', 'd3', 'd4']);
    expect(f.armed()[0]).toMatchObject({ at: at(7), title: 'Easy run', body: 'Shoes by the door.', data: { actionId: 'd0' } });
  });

  it('caps the window at MOMENTS_AHEAD moments', () => {
    const many = Array.from({ length: MOMENTS_AHEAD + 5 }, (_, i) => action({ id: `m${i}`, date: `2026-02-${String(i + 1).padStart(2, '0')}` }));
    expect(toArm(many, NIGHT_BEFORE)).toHaveLength(MOMENTS_AHEAD);
  });

  it('re-arms from what is left once one has fired, and drops what was armed before', async () => {
    const f = fakeNotifier();
    await arm(f.n, toArm(week, NIGHT_BEFORE));
    await arm(f.n, toArm(week, at(7, 1)));
    expect(f.armed().map((r) => r.data.actionId)).toEqual(['d1', 'd2', 'd3', 'd4']);
  });

  it('arms a reminder with no text yet with no body, never an invented one', async () => {
    const f = fakeNotifier();
    await arm(f.n, toArm([action({ reminderText: null })], NIGHT_BEFORE));
    expect(f.armed()[0]).toMatchObject({ title: 'Easy run', body: null });
  });

  it('re-arms without the Action once it is checked in', async () => {
    const f = fakeNotifier();
    const checkIn = { state: 'skipped' as const, minutes: null, reasons: [], note: null, tappedAt: '2026-01-06T00:00:00Z' };
    await arm(f.n, toArm([{ ...week[0]!, checkIn }, ...week.slice(1)], NIGHT_BEFORE));
    expect(f.armed()[0]!.data.actionId).toBe('d1');
  });

  it('arms nothing when there is nothing upcoming', async () => {
    const f = fakeNotifier();
    await arm(f.n, toArm(week, at(12, 0, 20)));
    expect(f.armed()).toEqual([]);
  });
});

describe('the headless Check-in task', () => {
  const response = (actionIdentifier: string, dataString: string | null | undefined, userText?: string): ReminderResponse => ({
    actionIdentifier, userText, notification: { request: { identifier: 'n-1', content: { dataString } } },
  });

  function setup() {
    const storage = createMemoryStorage();
    const sent: OutboxItem[] = [];
    const remote: Remote = {
      async send(item) { sent.push(item); throw new Error('offline'); },
      async pull(): Promise<Pull> { throw new Error('offline'); },
    };
    let n = 0;
    const sync = createSync({ storage, remote, now: () => new Date('2026-01-07T07:05:00Z'), newId: () => `id-${++n}` });
    const offline = createOfflineToday({ storage, sync });
    const dismissed: string[] = [];
    let rearmed = 0;
    const deps = {
      checkIn: offline, now: () => new Date('2026-01-07T07:05:00Z'), newId: () => `ci-${++n}`,
      dismiss: async (id: string) => void dismissed.push(id), rearm: async () => void rearmed++,
    };
    return { storage, deps, dismissed, rearmed: () => rearmed };
  }
  const data = JSON.stringify({ actionId: 'a1' });

  it('reads the Action from content.dataString', () => {
    expect(parseReminderData(data)).toEqual({ actionId: 'a1' });
    expect(parseReminderData(undefined)).toBeNull();
    expect(parseReminderData('nope')).toBeNull();
    expect(parseReminderData('{"actionId":3}')).toBeNull();
  });

  it('turns Done into an outbox Check-in, clears the notification and re-arms', async () => {
    const s = setup();
    expect(await handleReminderResponse(response('done', data), s.deps)).toBe('recorded');
    const [item] = await s.storage.outbox();
    expect(item).toMatchObject({ kind: 'check_in', payload: { scheduledActionId: 'a1', state: 'done', minutes: null, tappedAt: '2026-01-07T07:05:00.000Z' } });
    expect(item!.clientId).toMatch(/^ci-/);
    expect(s.dismissed).toEqual(['n-1']);
    expect(s.rearmed()).toBe(1);
  });

  it('takes the minutes for Partly from the inline reply, and Skip needs none', async () => {
    const s = setup();
    await handleReminderResponse(response('partly', data, '20 min'), s.deps);
    await handleReminderResponse(response('skip', JSON.stringify({ actionId: 'a2' })), s.deps);
    const items = await s.storage.outbox();
    expect(items.map((i) => [i.payload.scheduledActionId, i.payload.state, i.payload.minutes])).toEqual([['a1', 'partly', 20], ['a2', 'skipped', null]]);
  });

  it('records nothing for a body tap, a Partly with no minutes, or data that is not ours', async () => {
    const s = setup();
    expect(await handleReminderResponse(response('expo.modules.notifications.actions.DEFAULT', data), s.deps)).toBe('ignored');
    expect(await handleReminderResponse(response('partly', data, 'a bit'), s.deps)).toBe('ignored');
    expect(await handleReminderResponse(response('done', null), s.deps)).toBe('ignored');
    expect(await s.storage.outbox()).toEqual([]);
    expect(s.dismissed).toEqual([]);
  });

  it('parses minutes and hours', () => {
    expect([parseMinutes('20'), parseMinutes('15 min'), parseMinutes('1.5h'), parseMinutes('0'), parseMinutes(undefined)]).toEqual([20, 15, 90, null, null]);
  });
});

describe('permission prompts', () => {
  function platform(grants: Partial<Record<PermissionStep, boolean>>, agree = true) {
    const log: string[] = [];
    const state = { notifications: false, exactAlarms: false, ...grants };
    const p: PermissionPlatform = {
      async granted(step) { return state[step]; },
      async explain(step) { log.push(`explain:${step}`); return agree; },
      async request(step) { log.push(`request:${step}`); state[step] = true; },
    };
    return { p, log };
  }

  it('asks for notifications, then exact alarms, one at a time, each explained first', async () => {
    const { p, log } = platform({});
    expect(await askForReminderPermissions(p)).toEqual(['notifications', 'exactAlarms']);
    expect(log).toEqual(['explain:notifications', 'request:notifications', 'explain:exactAlarms', 'request:exactAlarms']);
  });

  it('skips what is granted already', async () => {
    const { p, log } = platform({ notifications: true });
    await askForReminderPermissions(p);
    expect(log).toEqual(['explain:exactAlarms', 'request:exactAlarms']);
  });

  it('does not raise the OS prompt when the user declines the explanation, and stops without notifications', async () => {
    const { p, log } = platform({}, false);
    expect(await askForReminderPermissions(p)).toEqual(['notifications']);
    expect(log).toEqual(['explain:notifications']);
  });
});

describe('asking once after the first Plan', () => {
  function setup() {
    let asked = false;
    const log: string[] = [];
    const p: PermissionPlatform = {
      async granted() { return false; },
      async explain(step) { log.push(step); return false; },
      async request() {},
    };
    const flag = { get: async () => asked, set: async () => void (asked = true) };
    return { p, flag, log };
  }

  it('asks on the first approval and never again, even if the user declined', async () => {
    const { p, flag, log } = setup();
    expect(await askOnceAfterFirstPlan(p, flag)).toEqual(['notifications']);
    expect(await askOnceAfterFirstPlan(p, flag)).toEqual([]);
    expect(log).toEqual(['notifications']);
  });
});

describe('Follow-ups armed with the reminders', () => {
  const armed: ArmedReminder[] = [];
  const notifier: Notifier = { async cancelAll() { armed.length = 0; }, async schedule(r) { armed.push(r); }, async dismiss() {} };
  const ctx = (over = {}) => ({ now: NIGHT_BEFORE, interactedAt: null, paused: false, ...over });
  const armAll = async (actions: ScheduledActionView[], c = ctx()) => {
    const followUps = upcomingFollowUps(actions, c).map((f) => ({
      groupKey: `follow-up-${f.actionId}`, at: f.at, title: f.title, body: en.reminders.followUp[f.tone], data: { actionId: f.actionId },
    }));
    await arm(notifier, toArm(actions, c.now), followUps);
  };

  it('arms one Follow-up per Action at the end of its Slot, in the Aspiration\'s skip tone', async () => {
    await armAll([action({ onSkip: 'ask_why' })]);
    expect(armed.map((r) => [r.at, r.data.actionId, r.body])).toEqual([
      [at(7), 'a1', 'Shoes by the door.'],
      [at(7, 45), 'a1', en.reminders.followUp.ask_why],
    ]);
  });

  it('falls back to a gentle check when the Action carries no tone', async () => {
    await armAll([action()]);
    expect(armed[1]!.body).toBe(en.reminders.followUp.gentle_check);
  });

  it('arms none when the user interacted that day, or when the Action is switched off', async () => {
    await armAll([action()], ctx({ now: at(6, 0), interactedAt: at(6, 0) }));
    expect(armed.map((r) => r.at)).toEqual([at(7)]);
    await armAll([action({ reminderOn: false })]);
    expect(armed).toEqual([]);
  });

  it('arms none for a habit that can still happen later that week', async () => {
    await armAll([action({ kind: 'habit' }), action({ id: 'a2', kind: 'habit', date: '2026-01-09' })]);
    expect(armed.filter((r) => r.at.getDate() === 7 && r.at.getHours() === 7 && r.at.getMinutes() === 45)).toEqual([]);
  });

  it('arms none during a Pause or after two unreported Actions', async () => {
    await armAll([action()], ctx({ paused: true }));
    expect(armed).toHaveLength(1);
    const earlier = [action({ id: 'p1', date: '2026-01-05' }), action({ id: 'p2', date: '2026-01-06' })];
    await armAll([...earlier, action()], ctx({ now: at(6, 0, 7) }));
    expect(armed.map((r) => r.data.actionId)).toEqual(['a1']);
  });
});
