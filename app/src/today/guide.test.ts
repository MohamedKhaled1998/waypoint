import { guideSheet, type Guide } from './guide';
import type { ScheduledActionView } from './today';

const guide: Guide = {
  body: 'Warm up, run easy.',
  targets: [{ what: 'Run time', low: 20, high: 30, unit: 'min', why: 'a gentle start' }],
  withheld: [],
  referral: '',
};

const action = (id: string, date: string, state?: 'done' | 'partly' | 'skipped', over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id, aspirationId: 'asp', date, slot: null, slotStart: null, slotEnd: null, title: 'Easy run', minutes: 30,
  checkIn: state ? { state, minutes: null, reasons: [], note: null, tappedAt: `${date}T08:00:00Z` } : null,
  guide, ...over,
});

describe('the Action sheet How-to', () => {
  it('has nothing to show for an Action with no Guide', () => {
    expect(guideSheet(action('a', '2026-01-12', undefined, { guide: null }), [])).toBeNull();
  });

  it('is collapsed, and opens by itself only the first two times the Action is done', () => {
    const history = [action('a', '2026-01-07', 'done'), action('b', '2026-01-09', 'done'), action('c', '2026-01-12'), action('d', '2026-01-14')];
    expect(guideSheet(history[0]!, history)?.openByItself).toBe(true); // the first time
    expect(guideSheet(history[2]!, history)?.openByItself).toBe(false); // two already done
    expect(guideSheet(action('a', '2026-01-07'), [action('a', '2026-01-07')])?.openByItself).toBe(true);
    const one = [action('a', '2026-01-07', 'done'), action('b', '2026-01-09')];
    expect(guideSheet(one[1]!, one)?.openByItself).toBe(true); // the second time
  });

  it('counts only Done Check-ins on the same Action of the same Aspiration', () => {
    const history = [
      action('a', '2026-01-07', 'partly'), action('b', '2026-01-08', 'skipped'),
      action('c', '2026-01-09', 'done', { title: 'Long run' }), action('d', '2026-01-10', 'done', { aspirationId: 'other' }),
      action('e', '2026-01-12'),
    ];
    expect(guideSheet(history[4]!, history)?.openByItself).toBe(true);
  });

  it('shows the referral as its own card under the Guide only while the How-to opens by itself', () => {
    const withReferral = { ...guide, withheld: [{ area: 'leg load', reason: 'knee' }], referral: 'A word with your physio first.' };
    const first = action('a', '2026-01-07', undefined, { guide: withReferral });
    const later = action('c', '2026-01-14', undefined, { guide: withReferral });
    const history = [action('a0', '2026-01-01', 'done', { guide: withReferral }), action('b0', '2026-01-03', 'done', { guide: withReferral })];
    expect(guideSheet(first, [first])?.referralCard).toBe('A word with your physio first.');
    expect(guideSheet(later, [...history, later])?.referralCard).toBeNull();
  });
});
