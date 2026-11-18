import { createSync, type Pull, type Remote } from '../sync/engine';
import { createMemoryStorage, type OutboxItem } from '../sync/storage';
import { createCheckInTask } from './checkin';
import { createOfflineToday } from './offline';
import { moveOptions } from './sheet';
import { buildToday, type ScheduledActionView } from './today';

beforeEach(() => jest.useFakeTimers()); // the Undo window's timer
afterEach(() => {
  jest.clearAllTimers();
  jest.useRealTimers();
});

const action = (over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id: 'a1', aspirationId: 'asp', date: '2026-01-07', slotId: 's1', slot: 'weekday mornings', slotStart: '07:00', slotEnd: '07:45',
  title: 'Easy run', minutes: 30, checkIn: null, ...over,
});
const slot = { id: 's1', label: 'weekday mornings', days: 'mon,wed,fri', start: '07:00', end: '07:45' };

/** A server that is down until `up` is set, and keeps what it was sent. */
function setup() {
  const storage = createMemoryStorage();
  const server = { up: false, received: [] as OutboxItem[], pulls: 0 };
  const remote: Remote = {
    async send(item) {
      if (!server.up) throw new Error('offline');
      if (!server.received.some((r) => r.clientId === item.clientId)) server.received.push(item);
      return { kind: 'sent' };
    },
    async pull(cursor): Promise<Pull> {
      if (!server.up) throw new Error('offline');
      server.pulls++;
      return { cursor: cursor + 1, more: false, changes: [], slots: [] };
    },
  };
  let n = 0;
  const sync = createSync({ storage, remote, now: () => new Date('2026-01-07T07:30:00Z'), newId: () => `id-${++n}` });
  const offline = createOfflineToday({ storage, sync });
  return { storage, server, sync, offline, newId: () => `ci-${++n}` };
}

describe('with the server unreachable', () => {
  it('shows Today from the cache and takes Check-ins and a move, then sends them in order once it is back', async () => {
    const { storage, server, sync, offline, newId } = setup();
    await storage.apply(
      { upserts: [{ kind: 'scheduled_action', id: 'a1', data: action() }, { kind: 'scheduled_action', id: 'a2', data: action({ id: 'a2', title: 'Stretch' }) }, { kind: 'slot', id: 's1', data: slot }], removes: [] },
      3,
    );

    const task = createCheckInTask({ api: offline, now: () => new Date('2026-01-07T07:40:00Z'), newId });
    await task.quick(action(), 'done'); // the Now card
    await offline.put({ scheduledActionId: 'a2', clientId: newId(), state: 'partly', minutes: 10, reasons: ['tired'], note: null, tappedAt: '2026-01-07T08:00:00Z' }); // the Action sheet
    await offline.move('a2', '2026-01-09', 's1');

    const today = buildToday(await offline.actions(), { date: '2026-01-07', time: '08:00' });
    expect(today.summary).toMatchObject({ checkedIn: 1, total: 1 });
    expect((await offline.actions()).find((a) => a.id === 'a2')).toMatchObject({ date: '2026-01-09', checkIn: { state: 'partly', minutes: 10 } });

    await sync.sync();
    expect(server.received).toEqual([]);
    expect((await storage.outbox()).map((i) => i.kind)).toEqual(['check_in', 'check_in', 'move_action']);

    server.up = true;
    expect(await sync.sync()).toBe('synced');
    expect(server.received.map((i) => i.kind)).toEqual(['check_in', 'check_in', 'move_action']);
    expect(server.received[0]!.payload).toMatchObject({ scheduledActionId: 'a1', state: 'done' });
    expect(server.received[2]!.payload).toEqual({ scheduledActionId: 'a2', date: '2026-01-09', slotId: 's1' });
    expect(await storage.outbox()).toEqual([]);
    expect(server.pulls).toBe(1);
  });

  it('undoes a Check-in through the outbox too', async () => {
    const { storage, offline, newId } = setup();
    await storage.apply({ upserts: [{ kind: 'scheduled_action', id: 'a1', data: action() }], removes: [] });
    const task = createCheckInTask({ api: offline, now: () => new Date(), newId });
    await task.quick(action(), 'skipped');
    await task.undo();
    expect((await offline.actions())[0]!.checkIn).toBeNull();
    expect((await storage.outbox()).map((i) => i.kind)).toEqual(['check_in', 'undo_check_in']);
  });
});

describe('past the cache', () => {
  it('knows the last date it has Actions for, and never invents any', () => {
    const today = buildToday([action(), action({ id: 'a2', date: '2026-01-21' })], { date: '2026-01-22', time: '08:00' });
    expect(today.plannedUntil).toBe('2026-01-21');
    expect(today.timeline).toEqual([]);
    expect(buildToday([], { date: '2026-01-22', time: '08:00' }).plannedUntil).toBeNull();
  });
});

describe('moving one Action', () => {
  it('offers the Slots the user has, on their days in the next two weeks, except where it already is', () => {
    const options = moveOptions({ date: '2026-01-07', slotId: 's1' }, [slot], { date: '2026-01-07', time: '06:00' });
    expect(options.map((o) => o.date)).toEqual(['2026-01-09', '2026-01-12', '2026-01-14', '2026-01-16', '2026-01-19']);
    expect(options[0]).toEqual({ date: '2026-01-09', slotId: 's1', label: 'weekday mornings', start: '07:00' });
  });

  it('leaves out a time today that has already started', () => {
    const options = moveOptions({ date: '2026-01-09', slotId: 's1' }, [slot], { date: '2026-01-07', time: '07:30' });
    expect(options[0]!.date).toBe('2026-01-12');
    expect(options.map((o) => o.date)).not.toContain('2026-01-07');
  });
});
