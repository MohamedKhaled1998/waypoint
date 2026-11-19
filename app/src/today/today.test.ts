import { buildToday, resultLabel, type ScheduledActionView } from './today';

const action = (id: string, over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id,
  aspirationId: 'a1',
  date: '2026-01-07',
  slot: 'weekday mornings',
  slotStart: '07:00',
  slotEnd: '07:45',
  title: 'Easy run',
  minutes: 30,
  checkIn: null,
  ...over,
});
const checked = (state: 'done' | 'partly' | 'skipped', minutes: number | null = null) => ({
  state,
  minutes,
  reasons: [],
  note: null,
  tappedAt: '2026-01-07T07:30:00Z',
});
const at = (time: string, date = '2026-01-07') => ({ date, time });

describe('Today', () => {
  it('puts an overdue Action with no Check-in on the Now card', () => {
    const today = buildToday([action('a', { slotStart: '07:00' }), action('b', { slotStart: '18:00' })], at('09:00'));
    expect(today.state).toBe('active');
    expect(today.nowCard).toEqual({ kind: 'overdue', action: expect.objectContaining({ id: 'a' }) });
  });

  it('falls back to the next Action when nothing is overdue', () => {
    const today = buildToday([action('a', { checkIn: checked('done') }), action('b', { slotStart: '18:00' })], at('09:00'));
    expect(today.nowCard).toEqual({ kind: 'next', action: expect.objectContaining({ id: 'b' }) });
  });

  it('orders the timeline by time and places the now line among it', () => {
    const today = buildToday(
      [action('late', { slotStart: '18:00' }), action('early', { slotStart: '07:00' }), action('tomorrow', { date: '2026-01-08' })],
      at('09:00'),
    );
    expect(today.timeline.map((a) => a.id)).toEqual(['early', 'late']);
    expect(today.nowLineBefore).toBe(1);
  });

  it('sums the summary line from Check-ins: Partly counts the minutes spent', () => {
    const today = buildToday(
      [
        action('a', { minutes: 30, checkIn: checked('done') }),
        action('b', { minutes: 30, slotStart: '08:00', checkIn: checked('partly', 20) }),
        action('c', { minutes: 25, slotStart: '12:00', checkIn: checked('skipped') }),
        action('d', { minutes: 0, slotStart: '18:00' }),
      ],
      at('09:00'),
    );
    expect(today.summary).toEqual({ checkedIn: 3, total: 4, minutesDone: 50, minutesPlanned: 85 });
  });

  it('says all checked in, with tomorrow\'s first Action', () => {
    const today = buildToday(
      [
        action('a', { checkIn: checked('done') }),
        action('t2', { date: '2026-01-09', slotStart: '07:00' }),
        action('t1', { date: '2026-01-08', slotStart: '18:00' }),
      ],
      at('09:00'),
    );
    expect(today.state).toBe('allCheckedIn');
    expect(today.nowCard).toBeNull();
    expect(today.tomorrowFirst?.id).toBe('t1');
  });

  it('is a rest day when Actions exist but none today, and empty when there are none', () => {
    expect(buildToday([action('x', { date: '2026-01-09' })], at('09:00')).state).toBe('rest');
    expect(buildToday([], at('09:00')).state).toBe('none');
  });
});

describe('result label', () => {
  it('reads Done, Partly with minutes of planned, and Skipped', () => {
    expect(resultLabel(action('a', { checkIn: checked('done') }))).toEqual({ state: 'done' });
    expect(resultLabel(action('a', { checkIn: checked('partly', 20) }))).toEqual({ state: 'partly', minutes: 20, planned: 30 });
    expect(resultLabel(action('a', { checkIn: checked('skipped') }))).toEqual({ state: 'skipped' });
    expect(resultLabel(action('a'))).toBeNull();
  });
});
