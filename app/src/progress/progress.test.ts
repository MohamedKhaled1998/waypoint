import { en } from '../i18n/en';
import { hoursLabel, projectionLine, stepFractions, type Projection } from './progress';

/** A minimal t over the English strings, with {{name}} interpolation. */
function t(key: string, values: Record<string, string | number> = {}): string {
  const found = key.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown> | undefined)?.[k], en);
  if (typeof found !== 'string') throw new Error(`missing string ${key}`);
  return found.replace(/\{\{(\w+)\}\}/g, (_, n: string) => String(values[n]));
}

const part = (p: 'early' | 'mid' | 'late', month: number) => ({ part: p, month });

describe('Projection in words', () => {
  it('says a narrow range as "around" one time', () => {
    const p: Projection = { kind: 'around', from: part('mid', 6), to: part('mid', 6) };
    expect(projectionLine(p, t)).toBe('around mid-June');
  });

  it('says a range inside one month as "between early and late June"', () => {
    const p: Projection = { kind: 'between', from: part('early', 6), to: part('late', 6) };
    expect(projectionLine(p, t)).toBe('somewhere between early and late June');
  });

  it('says a range across months with both months named', () => {
    const p: Projection = { kind: 'between', from: part('late', 3), to: part('early', 5) };
    expect(projectionLine(p, t)).toBe('somewhere between late March and early May');
  });

  it('says "too early to tell" instead of a date, and never uses a percentage', () => {
    const line = projectionLine({ kind: 'too_early' }, t);
    expect(line).toBe('too early to tell — you’re on the Plan so far');
    expect(line).not.toMatch(/%/);
  });
});

describe('Progress numbers', () => {
  it('shows hours with at most one decimal', () => {
    expect(hoursLabel(90)).toBe('1.5');
    expect(hoursLabel(120)).toBe('2');
    expect(hoursLabel(0)).toBe('0');
  });

  it('fills the step bar with reached Milestones', () => {
    expect(stepFractions(1, 4)).toEqual([true, false, false, false]);
    expect(stepFractions(0, 0)).toEqual([]);
  });
});
