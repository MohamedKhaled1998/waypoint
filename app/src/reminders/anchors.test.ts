import { createSync, type Pull, type Remote } from '../sync/engine';
import { createMemoryStorage, type OutboxItem } from '../sync/storage';
import { createOfflineToday } from '../today/offline';
import type { ScheduledActionView } from '../today/today';
import { groupByMoment, toArm, upcomingReminders } from './arming';
import { arm, type ArmedReminder, type Notifier } from './notifier';

const slotAction = (over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id: 'run', aspirationId: 'asp', date: '2026-01-07', slotId: 's1', slot: 'weekday mornings', slotStart: '07:00', slotEnd: '07:45',
  title: 'Easy run', minutes: 30, checkIn: null, reminderKind: 'now', reminderOn: true, ...over,
});
/** An Anchor habit's Action: no Slot, at its Anchor's usual time. */
const anchorAction = (id: string, time: string, over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id, aspirationId: 'asp', date: '2026-01-07', slotId: null, slot: null, slotStart: time, slotEnd: null, title: `Habit ${id}`, minutes: 0,
  checkIn: null, reminderKind: 'now', reminderOn: true, habitId: `h-${id}`, anchor: { id: 'an', words: 'after dinner', time }, ...over,
});
const at = (h: number, m = 0) => new Date(2026, 0, 7, h, m);
const MORNING = at(6, 0);

describe('grouping Anchor reminders', () => {
  const groups = (actions: ScheduledActionView[]) => groupByMoment(upcomingReminders(actions, MORNING)).map((g) => g.reminders.map((r) => r.actionId));

  it('joins Anchor reminders due within 30 minutes into one grouped notification', () => {
    expect(groups([anchorAction('walk', '19:30'), anchorAction('tea', '19:50'), anchorAction('read', '20:00')])).toEqual([['walk', 'tea', 'read']]);
  });

  it('keeps Anchor reminders more than 30 minutes apart in separate notifications', () => {
    expect(groups([anchorAction('lunch', '12:30'), anchorAction('walk', '19:30'), anchorAction('late', '20:01')])).toEqual([['lunch'], ['walk'], ['late']]);
  });

  it('joins an Anchor reminder to any other reminder due within 30 minutes', () => {
    expect(groups([slotAction({ slotStart: '19:20' }), anchorAction('walk', '19:30')])).toEqual([['run', 'walk']]);
  });

  it('leaves Slot reminders alone: they group only at the same minute', () => {
    expect(groups([slotAction({ id: 'a', slotStart: '07:00' }), slotAction({ id: 'b', slotStart: '07:20' })])).toEqual([['a'], ['b']]);
  });

  it('rings the whole group together, at its first reminder, and keeps it in the shade for a late Check-in', async () => {
    let armed: ArmedReminder[] = [];
    const n: Notifier = { async cancelAll() { armed = []; }, async schedule(r) { armed.push(r); }, async dismiss() {} };
    await arm(n, toArm([anchorAction('walk', '19:30'), anchorAction('tea', '19:50')], MORNING));
    expect(armed.map((r) => [r.data.actionId, r.at])).toEqual([['walk', at(19, 30)], ['tea', at(19, 30)]]);
    expect(armed.every((r) => r.sticky)).toBe(true);
  });

  it('does not keep a Slot reminder in the shade', async () => {
    let armed: ArmedReminder[] = [];
    const n: Notifier = { async cancelAll() {}, async schedule(r) { armed.push(r); }, async dismiss() {} };
    await arm(n, toArm([slotAction()], MORNING));
    expect(armed[0]!.sticky).toBe(false);
  });
});

describe('one notification per Action', () => {
  it('schedules an Anchor Action once, sticky in the shade, with nothing at a later time', async () => {
    const armed: ArmedReminder[] = [];
    const n: Notifier = { async cancelAll() {}, async schedule(r) { armed.push(r); }, async dismiss() {} };
    await arm(n, toArm([anchorAction('walk', '19:30', { slotEnd: '20:30' })], MORNING));
    expect(armed).toHaveLength(1);
    expect(armed[0]!.sticky).toBe(true);
    expect(armed[0]!.at).toEqual(at(19, 30));
  });

  it('schedules a Slot Action once too, at its start and not at its end', async () => {
    const armed: ArmedReminder[] = [];
    const n: Notifier = { async cancelAll() {}, async schedule(r) { armed.push(r); }, async dismiss() {} };
    await arm(n, toArm([slotAction()], MORNING));
    expect(armed.map((r) => r.at)).toEqual([at(7, 0)]);
  });
});

describe('switching a habit reminder off', () => {
  function setup() {
    const storage = createMemoryStorage();
    const sent: OutboxItem[] = [];
    const remote: Remote = {
      async send(item) { sent.push(item); throw new Error('offline'); },
      async pull(): Promise<Pull> { throw new Error('offline'); },
    };
    let n = 0;
    const sync = createSync({ storage, remote, now: () => new Date('2026-01-07T08:00:00Z'), newId: () => `id-${++n}` });
    return { storage, sync, offline: createOfflineToday({ storage, sync }) };
  }

  it('stops that habit from the cache at once, queues it for the server, and leaves other habits ringing', async () => {
    const { storage, sync, offline } = setup();
    const walk = (id: string, date: string) => anchorAction(id, '19:30', { date, title: 'Walk after dinner', habitId: 'h-walk' });
    for (const a of [walk('w1', '2026-01-07'), walk('w2', '2026-01-09'), anchorAction('read', '20:00'), slotAction()])
      await storage.apply({ upserts: [{ kind: 'scheduled_action', id: a.id, data: a }], removes: [] });

    await offline.setHabitReminder(walk('w1', '2026-01-07'), false, { today: '2026-01-07' });

    const cached = await offline.actions();
    expect(upcomingReminders(cached, MORNING).map((r) => r.actionId).sort()).toEqual(['read', 'run']);
    const [item] = await sync.outbox();
    expect(item).toMatchObject({ kind: 'habit_reminder', payload: { habitId: 'h-walk', on: false, fromOffer: false } });
  });

  it('switches it back on the same way, and marks taking the Coach\'s offer', async () => {
    const { storage, sync, offline } = setup();
    const a = anchorAction('w1', '19:30', { reminderOn: false, title: 'Walk after dinner', habitId: 'h-walk' });
    await storage.apply({ upserts: [{ kind: 'scheduled_action', id: a.id, data: a }], removes: [] });
    await offline.setHabitReminder(a, true, { today: '2026-01-07' });
    expect(upcomingReminders(await offline.actions(), MORNING)).toHaveLength(1);
    await offline.setHabitReminder(a, false, { today: '2026-01-07', fromOffer: true });
    expect((await sync.outbox()).map((i) => i.payload)).toEqual([
      { habitId: 'h-walk', on: true, fromOffer: false }, { habitId: 'h-walk', on: false, fromOffer: true },
    ]);
  });
});
