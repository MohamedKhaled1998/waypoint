import { createCheckInTask, UNDO_MS, type CheckInApi } from './checkin';
import type { CheckInRequest, CheckInView } from './today';

function fake(opts: { failing?: Set<string> } = {}) {
  const calls: string[] = [];
  const api: CheckInApi = {
    async put(req: CheckInRequest) {
      if (opts.failing?.has('put')) throw new Error('offline');
      calls.push(`put ${req.scheduledActionId} ${req.state}`);
      return { state: req.state, minutes: req.minutes, reasons: req.reasons, note: req.note, tappedAt: req.tappedAt } satisfies CheckInView;
    },
    async undo(id) {
      if (opts.failing?.has('undo')) throw new Error('offline');
      calls.push(`undo ${id}`);
    },
  };
  let n = 0;
  const task = createCheckInTask({ api, now: () => new Date('2026-01-07T07:30:00Z'), newId: () => `c${++n}`, onChange: () => calls.push('change'), onError: () => calls.push('error') });
  return { calls, task };
}

describe('quick Check-in', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('sends the Check-in at once and offers Undo for 4 seconds', async () => {
    const { calls, task } = fake();
    await task.quick({ id: 'a', checkIn: null }, 'done');
    expect(calls).toContain('put a done');
    expect(task.undoable()).toEqual({ scheduledActionId: 'a', state: 'done' });
    jest.advanceTimersByTime(UNDO_MS - 1);
    expect(task.undoable()).not.toBeNull();
    jest.advanceTimersByTime(1);
    expect(task.undoable()).toBeNull();
  });

  it('Undo within the window removes a first Check-in', async () => {
    const { calls, task } = fake();
    await task.quick({ id: 'a', checkIn: null }, 'skipped');
    jest.advanceTimersByTime(3000);
    await task.undo();
    expect(calls).toContain('undo a');
    expect(task.undoable()).toBeNull();
  });

  it('Undo of a changed Check-in restores the earlier one with a newer tap', async () => {
    const { calls, task } = fake();
    const before = { state: 'partly' as const, minutes: 10, reasons: ['tired'], note: null, tappedAt: '2026-01-07T07:00:00Z' };
    await task.quick({ id: 'a', checkIn: before }, 'done');
    await task.undo();
    expect(calls.filter((c) => c.startsWith('put'))).toEqual(['put a done', 'put a partly']);
    expect(calls).not.toContain('undo a');
  });

  it('a new quick Check-in replaces the earlier Undo', async () => {
    const { task } = fake();
    await task.quick({ id: 'a', checkIn: null }, 'done');
    await task.quick({ id: 'b', checkIn: null }, 'done');
    expect(task.undoable()?.scheduledActionId).toBe('b');
  });

  it('once the window has closed Undo does nothing', async () => {
    const { calls, task } = fake();
    await task.quick({ id: 'a', checkIn: null }, 'done');
    jest.advanceTimersByTime(UNDO_MS);
    await task.undo();
    expect(calls.some((c) => c.startsWith('undo'))).toBe(false);
  });

  it('Done then Skipped quickly, then Undo, restores the state from before both', async () => {
    const { calls, task } = fake();
    await task.quick({ id: 'a', checkIn: null }, 'done');
    await task.quick({ id: 'a', checkIn: null }, 'skipped');
    await task.undo();
    expect(calls).toContain('undo a');
    expect(calls.filter((c) => c.startsWith('put'))).toEqual(['put a done', 'put a skipped']);
  });

  it('keeps the Undo on offer when the Undo call fails', async () => {
    const failing = new Set<string>();
    const { calls, task } = fake({ failing });
    await task.quick({ id: 'a', checkIn: null }, 'done');
    failing.add('undo');
    await task.undo();
    expect(calls).toContain('error');
    expect(task.undoable()).toEqual({ scheduledActionId: 'a', state: 'done' });
    failing.clear();
    await task.undo();
    expect(task.undoable()).toBeNull();
    expect(calls.filter((c) => c === 'undo a')).toHaveLength(1);
  });

  it('reports a failed quick Check-in without offering an Undo', async () => {
    const { calls, task } = fake({ failing: new Set(['put']) });
    await expect(task.quick({ id: 'a', checkIn: null }, 'done')).resolves.toBeUndefined();
    expect(calls).toContain('error');
    expect(task.undoable()).toBeNull();
  });
});
