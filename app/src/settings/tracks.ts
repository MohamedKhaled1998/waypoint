/** The pure parts of the Settings page: what its drag tracks and steppers mean. */

export const SNAP = 15;
export const DAY = 1440;
export const SLOT_FROM = 5 * 60;
export const SLOT_TO = DAY;

export type Span = { start: number; end: number };
export type Handle = 'start' | 'end';

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));
const snap = (minutes: number) => Math.round(minutes / SNAP) * SNAP;
const pad = (n: number) => String(n).padStart(2, '0');

/** "07:15", and "24:00" for the end of the day. */
export function timeLabel(minutes: number): string {
  return `${pad(Math.floor(minutes / 60))}:${pad(minutes % 60)}`;
}

/** Minutes after midnight of "HH:mm"; the server keeps the end of the day as 23:59, which the track calls 24:00. */
export function minutesOf(time: string): number {
  const [h, m] = time.split(':').map(Number);
  const minutes = h! * 60 + m!;
  return minutes === DAY - 1 ? DAY : minutes;
}

export function slotWindow(start: string, end: string): Span {
  return { start: minutesOf(start), end: minutesOf(end) };
}

/** What the server is sent: 24:00 stays "24:00" and is stored as 23:59 there. */
export function slotWindowToText(w: Span): { start: string; end: string } {
  return { start: timeLabel(w.start), end: timeLabel(w.end) };
}

/** A drag of one handle of the Slot track: snapped to 15 minutes, inside 05:00–24:00, never closer than one step. */
export function moveSlotHandle(w: Span, handle: Handle, minutes: number): Span {
  const m = snap(minutes);
  return handle === 'start'
    ? { ...w, start: clamp(m, SLOT_FROM, w.end - SNAP) }
    : { ...w, end: clamp(m, w.start + SNAP, SLOT_TO) };
}

/** Hours a week a Slot holds: its window over each of its days. */
export function slotHours(days: string[], w: Span): number {
  return (days.length * (w.end - w.start)) / 60;
}

/** The noon-to-noon track: 0 is 12:00, 720 is midnight, 1440 is the next 12:00. */
export const QUIET_PRESETS = [
  { start: '22:00', end: '07:00' },
  { start: '23:00', end: '07:00' },
  { start: '21:00', end: '08:00' },
] as const;

const fromNoon = (time: string) => (minutesOf(time) - 720 + DAY) % DAY;
const toTime = (track: number) => timeLabel((track + 720) % DAY);

/** Where quiet hours sit on the track, or null when the span doesn't fit between two noons (a daytime span). */
export function quietToTrack(start: string, end: string): Span | null {
  const s = fromNoon(start);
  const e = fromNoon(end);
  return e > s ? { start: s, end: e } : null;
}

export function quietFromTrack(t: Span): { start: string; end: string } {
  return { start: toTime(t.start), end: toTime(t.end % DAY) };
}

export function quietHours(t: Span): number {
  return (t.end - t.start) / 60;
}

/** A drag of one handle of the quiet-hours track: snapped to 15 minutes, a span is always one piece. */
export function moveQuietHandle(t: Span, handle: Handle, minutes: number): Span {
  const m = snap(minutes);
  return handle === 'start' ? { ...t, start: clamp(m, 0, t.end - SNAP) } : { ...t, end: clamp(m, t.start + SNAP, DAY) };
}

export const BUDGET_STEP = 0.5;
export const BUDGET_MIN = 1;
export const BUDGET_MAX = 40;

export function stepBudget(hours: number, direction: 1 | -1): number {
  return clamp(hours + direction * BUDGET_STEP, BUDGET_MIN, BUDGET_MAX);
}

export type Share = { aspirationId: string; shortName: string; hue: string; hours: number };

/** The Time budget bar: one segment per Aspiration, by its share of the budget; what is not shared stays empty. */
export function splitSegments(split: Share[], budget: number): { aspirationId: string; hue: string; fraction: number }[] {
  // Shares over the budget (until a Re-plan fits them) fill the bar between them rather than pushing the last ones out.
  const whole = Math.max(budget, split.reduce((sum, s) => sum + s.hours, 0));
  return split.map((s) => ({ aspirationId: s.aspirationId, hue: s.hue, fraction: whole > 0 ? Math.round((s.hours / whole) * 1000) / 1000 : 0 }));
}
