import { en } from '../i18n/en';
import { coachRoute, hoursLabel, isCoachIntent, projectionLine, settleOffer, stepFractions, type Projection, type SettleOption } from './progress';

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

describe('opening the Coach with an Aspiration in context', () => {
  it('scopes the Coach tab to the Aspiration and the choice', () => {
    expect(coachRoute('a1', 'Half marathon', 'keep_date_smaller_picture')).toEqual({
      pathname: '/coach',
      params: { aspirationId: 'a1', shortName: 'Half marathon', intent: 'keep_date_smaller_picture' },
    });
  });

  it('has a starter line in the glossary words for every intent', () => {
    for (const intent of ['change_dates', 'change_plan', 'move_date', 'keep_date_smaller_picture', 'make_more_room']) {
      expect(t(`coach.starters.${intent}`, { name: 'Half marathon' })).toContain('Half marathon');
    }
  });
});

describe('the Settle-for choice', () => {
  const base = { aspirationId: 'a1', shortName: 'Half marathon' };
  const choice = { options: ['move_date', 'keep_date_smaller_picture', 'make_more_room'] as SettleOption[], hasPicture: true };

  it('shows nothing when the server did not put the choice', () => {
    expect(settleOffer({ ...base, settleChoice: null })).toBeNull();
  });

  it('shows the muted line and one Coach route per option, each with the Aspiration and the choice', () => {
    const offer = settleOffer({ ...base, settleChoice: choice })!;
    expect(offer.mutedLine).toBe(true);
    expect(offer.options.map((o) => o.route.params)).toEqual([
      { aspirationId: 'a1', shortName: 'Half marathon', intent: 'move_date' },
      { aspirationId: 'a1', shortName: 'Half marathon', intent: 'keep_date_smaller_picture' },
      { aspirationId: 'a1', shortName: 'Half marathon', intent: 'make_more_room' },
    ]);
    expect(offer.options.every((o) => o.route.pathname === '/coach')).toBe(true);
  });

  it('knows which intents the Coach tab can open for', () => {
    expect(isCoachIntent('change_dates')).toBe(true);
    expect(isCoachIntent('nonsense')).toBe(false);
  });
});
