/** What the server says about an Aspiration's Measure. The trend and its verdict are decided there; the phone only words them. */

export type MeasureVerdict = 'too_few' | 'right_way' | 'slower' | 'flat' | 'wrong_way';

export type Reading = { id: string; date: string; value: number; source: 'plan_accepted' | 'manual' | 'chat_reply'; milestoneRef: string | null };

export type MeasureView =
  | { hasMeasure: false; readings: [] }
  | {
      hasMeasure: true;
      name: string;
      unit: string;
      start: number;
      hopedFor: number;
      hopedForDate: string;
      readings: Reading[];
      trend: { direction: 'up' | 'down' | 'flat'; change: number; since: string | null; verdict: MeasureVerdict };
    };

type T = (key: string, values?: Record<string, string | number>) => string;

/** "Down 5 bpm since 6 Oct · going the right way". Never a score: a check on the Plan. */
export function trendLine(trend: { direction: 'up' | 'down' | 'flat'; change: number; since: string | null; verdict: MeasureVerdict }, unit: string, since: string, t: T): string {
  if (trend.verdict === 'too_few' || !trend.since) return t('measure.trend.tooFew');
  const amount = Math.abs(trend.change);
  const move = trend.direction === 'flat' ? t('measure.trend.same', { since }) : t(`measure.trend.${trend.direction}`, { amount, unit, since });
  const where = trend.verdict === 'right_way' || trend.verdict === 'slower' ? 'rightWay' : trend.verdict === 'wrong_way' ? 'otherWay' : 'steady';
  return `${move} · ${t(`measure.trend.${where}`)}`;
}

/** Bar heights as shares of the tallest reading (or the hoped-for value), with a floor so a small value still shows. */
export function barShares(readings: Reading[], hopedFor: number): number[] {
  const top = Math.max(hopedFor, ...readings.map((r) => r.value), 1e-9);
  return readings.map((r) => Math.max(Math.min(Math.abs(r.value) / Math.abs(top), 1), 0.04));
}

/** A typed reading: "62", "62,5" or "62.5". Anything else is not a reading. */
export function parseReading(text: string): number | null {
  const clean = text.trim().replace(',', '.');
  if (!/^-?\d+(\.\d+)?$/.test(clean)) return null;
  const n = Number(clean);
  return Number.isFinite(n) && Math.abs(n) <= 1e9 ? n : null;
}
