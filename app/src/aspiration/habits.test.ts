import type { CheckInState, ScheduledActionView } from '../today/today';
import { buildHabits } from './habits';

const session = (id: string, date: string, over: Partial<ScheduledActionView> = {}, state?: CheckInState): ScheduledActionView => ({
  id, aspirationId: 'asp', date, slotId: null, slot: null, slotStart: '19:30', slotEnd: null, title: 'Walk after dinner', minutes: 0, habitId: 'h1',
  anchor: { id: 'an', words: 'after dinner', time: '19:30' }, targetMinutes: 20,
  checkIn: state ? { state, minutes: state === 'partly' ? 10 : null, reasons: [], note: null, tappedAt: `${date}T20:00:00Z` } : null, ...over,
});
const TODAY = '2026-01-14';

describe('the Habits tab', () => {
  const actions = [
    session('a', '2026-01-08', {}, 'done'),
    session('b', '2026-01-10', {}, 'skipped'),
    session('c', '2026-01-12', {}, 'partly'),
    session('d', '2026-01-13'), // went by with no Check-in
    session('e', '2026-01-14'), // tonight, still to come
    session('x', '2026-01-02', {}, 'done'), // older than the last 7 days
    session('o', '2026-01-12', { aspirationId: 'other', title: 'Stretch' }, 'done'),
  ];

  it('shows an Anchor habit with its Anchor, target and a dot for each of the last 7 days', () => {
    const [walk] = buildHabits(actions, 'asp', TODAY);
    expect(walk).toMatchObject({ title: 'Walk after dinner', anchor: { words: 'after dinner', time: '19:30' }, targetMinutes: 20 });
    expect(walk!.dots.map((d) => [d.date, d.state])).toEqual([
      ['2026-01-08', 'done'], ['2026-01-09', 'none'], ['2026-01-10', 'skipped'], ['2026-01-11', 'none'],
      ['2026-01-12', 'partly'], ['2026-01-13', 'missed'], ['2026-01-14', 'open'],
    ]);
  });

  it('counts the days done or partly out of the last 7', () => {
    expect(buildHabits(actions, 'asp', TODAY)[0]!.doneDays).toBe(2);
  });

  it('keeps only this Aspiration and lists a Slot habit with its Slot instead of an Anchor', () => {
    const run = session('r', '2026-01-14', { title: 'Easy run', habitId: 'h2', anchor: null, slot: 'weekday mornings', slotStart: '07:00', slotEnd: '07:45', minutes: 30, targetMinutes: null });
    const habits = buildHabits([...actions, run], 'asp', TODAY);
    expect(habits.map((h) => h.title)).toEqual(['Easy run', 'Walk after dinner']);
    expect(habits[0]).toMatchObject({ anchor: null, slot: 'weekday mornings' });
  });

  it('carries the reminder switch of the habit', () => {
    const off = [session('a', '2026-01-14', { reminderOn: false })];
    expect(buildHabits(off, 'asp', TODAY)[0]!.reminderOn).toBe(false);
  });
});
