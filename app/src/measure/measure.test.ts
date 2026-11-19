import { en } from '../i18n/en';
import { barShares, parseReading, trendLine } from './measure';

function t(key: string, values: Record<string, string | number> = {}): string {
  const found = key.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown> | undefined)?.[k], en);
  if (typeof found !== 'string') throw new Error(`missing string ${key}`);
  return found.replace(/\{\{(\w+)\}\}/g, (_, n: string) => String(values[n]));
}

describe('the Measure trend in words', () => {
  it('says how far it moved and that it is going the right way', () => {
    const trend = { direction: 'down', change: -5, since: '2026-10-06', verdict: 'right_way' } as const;
    expect(trendLine(trend, 'bpm', '6 Oct', t)).toBe('Down 5 bpm since 6 Oct · going the right way');
  });

  it('treats slower than hoped as still the right way: the Plan is not failed by it', () => {
    const trend = { direction: 'down', change: -2, since: '2026-10-06', verdict: 'slower' } as const;
    expect(trendLine(trend, 'bpm', '6 Oct', t)).toBe('Down 2 bpm since 6 Oct · going the right way');
  });

  it('says the other way without scolding', () => {
    const trend = { direction: 'up', change: 3, since: '2026-10-06', verdict: 'wrong_way' } as const;
    expect(trendLine(trend, 'bpm', '6 Oct', t)).toBe('Up 3 bpm since 6 Oct · going the other way');
  });

  it('asks for another reading when there is no trend yet', () => {
    const trend = { direction: 'flat', change: 0, since: '2026-10-06', verdict: 'too_few' } as const;
    expect(trendLine(trend, 'bpm', '6 Oct', t)).toBe('Add one more reading to see a trend.');
  });
});

describe('Measure readings', () => {
  it('accepts a comma or a point and refuses other text', () => {
    expect(parseReading(' 62,5 ')).toBe(62.5);
    expect(parseReading('62')).toBe(62);
    expect(parseReading('about 62')).toBeNull();
    expect(parseReading('')).toBeNull();
  });

  it('draws bars against the tallest of the readings and the hoped-for value', () => {
    const r = (value: number) => ({ id: String(value), date: '2026-10-06', value, source: 'manual' as const, milestoneRef: null });
    expect(barShares([r(70), r(35)], 60)).toEqual([1, 0.5]);
  });
});
