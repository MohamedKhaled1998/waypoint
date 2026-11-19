import type { CheckInView, ScheduledActionView } from '../today/today';
import { en } from '../i18n/en';
import { followUp, type FollowUpContext } from './followUp';

const checkIn = (tappedAt: string): CheckInView => ({ state: 'done', minutes: null, reasons: [], note: null, tappedAt });
const action = (over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id: 'a3', aspirationId: 'asp', date: '2026-01-07', slotId: 's1', slot: 'weekday mornings', slotStart: '07:00', slotEnd: '07:45',
  title: 'Buy running shoes', kind: 'task', minutes: 30, checkIn: null, reminderOn: true,
  ...over,
});
// Wednesday 2026-01-07, a quarter of an hour after the Slot ended.
const after = new Date(2026, 0, 7, 8, 0);
const ctx = (over: Partial<FollowUpContext> = {}): FollowUpContext => ({ now: after, interactedAt: null, paused: false, ...over });
const earlier = (n: number, over: Partial<ScheduledActionView> = {}) =>
  action({ id: `old${n}`, date: `2026-01-0${n}`, slotStart: '07:00', slotEnd: '07:45', title: `Earlier ${n}`, ...over });

describe('the Follow-up at the end of a Slot', () => {
  it('is sent once the Slot has ended for an Action nobody reported', () => {
    const f = followUp(action(), [action()], ctx());
    expect(f).toEqual({ send: true, at: new Date(2026, 0, 7, 7, 45), actionId: 'a3', tone: 'gentle_check' });
  });

  it('is not sent before the Slot ends', () => {
    expect(followUp(action(), [action()], ctx({ now: new Date(2026, 0, 7, 7, 30) }))).toEqual({ send: false, reason: 'not_yet' });
  });

  it('is not sent for an Action already checked in', () => {
    const done = action({ checkIn: checkIn('2026-01-07T07:50:00') });
    expect(followUp(done, [done], ctx())).toEqual({ send: false, reason: 'checked_in' });
  });

  it('is not sent when the user interacted that day', () => {
    expect(followUp(action(), [action()], ctx({ interactedAt: new Date(2026, 0, 7, 6, 0) }))).toEqual({ send: false, reason: 'interacted_today' });
    // Yesterday does not count.
    expect(followUp(action(), [action()], ctx({ interactedAt: new Date(2026, 0, 6, 20, 0) })).send).toBe(true);
  });

  it('is not sent during a Pause', () => {
    expect(followUp(action(), [action()], ctx({ paused: true }))).toEqual({ send: false, reason: 'paused' });
  });

  it('is not sent for a habit that can still happen later that week', () => {
    const run = action({ id: 'r1', title: 'Easy run', kind: 'habit' });
    const friday = action({ id: 'r2', title: 'Easy run', kind: 'habit', date: '2026-01-09' });
    expect(followUp(run, [run, friday], ctx())).toEqual({ send: false, reason: 'habit_can_still_happen' });
    // Next week's session is another week.
    const nextWeek = action({ id: 'r3', title: 'Easy run', kind: 'habit', date: '2026-01-12' });
    expect(followUp(run, [run, nextWeek], ctx()).send).toBe(true);
  });

  it('is not sent when the last two Actions went unreported', () => {
    const a = earlier(1), b = earlier(2);
    expect(followUp(action(), [a, b, action()], ctx())).toEqual({ send: false, reason: 'last_two_unreported' });
    // One of them reported is enough.
    const reported = earlier(2, { checkIn: checkIn('2026-01-02T07:50:00') });
    expect(followUp(action(), [a, reported, action()], ctx()).send).toBe(true);
  });

  it('is not sent with the reminder switched off, or for an Action with no Slot end (an Anchor)', () => {
    expect(followUp(action({ reminderOn: false }), [], ctx())).toEqual({ send: false, reason: 'reminder_off' });
    expect(followUp(action({ slotEnd: null }), [], ctx())).toEqual({ send: false, reason: 'no_slot_end' });
  });

  it('is written in the skip tone, with an easy out and none of the blame words', () => {
    for (const key of ['gentle_check', 'quiet_replan', 'ask_why'] as const) {
      const text = en.reminders.followUp[key];
      expect(text).not.toMatch(/missed|failed|streak/i);
      expect(text).toMatch(/no worries|that's fine|all good|if now isn't|no pressure/i);
    }
  });
});
