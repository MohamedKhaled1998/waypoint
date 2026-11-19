import type { CheckInState, ScheduledActionView } from '../today/today';

export type DotState = CheckInState | 'missed' | 'open' | 'none';
export type HabitDot = { date: string; state: DotState };

export type HabitRow = {
  title: string;
  /** The Anchor an Anchor habit is tied to; null for a habit on a Slot. */
  anchor: { words: string; time: string } | null;
  slot: string | null;
  targetMinutes: number | null;
  reminderOn: boolean;
  /** One per day for the last 7 days, oldest first. */
  dots: HabitDot[];
  /** Days among the last 7 with a done or partly session: "n of the last 7 days". */
  doneDays: number;
  /** One session of the habit, to act on its reminder switch. */
  sample: ScheduledActionView;
};

const addDays = (date: string, by: number) => {
  const [y, m, d] = date.split('-').map(Number) as [number, number, number];
  const x = new Date(Date.UTC(y, m - 1, d + by));
  return x.toISOString().slice(0, 10);
};

/** The Aspiration's habits from the phone's cache, each with its Anchor and the last 7 days as dots, in title order. */
export function buildHabits(actions: ScheduledActionView[], aspirationId: string, today: string): HabitRow[] {
  const days = Array.from({ length: 7 }, (_, i) => addDays(today, i - 6));
  const mine = actions.filter((a) => a.aspirationId === aspirationId);
  const titles = [...new Set(mine.map((a) => a.title))].filter((t) => mine.some((a) => a.title === t && a.habitId));
  return titles
    .sort((a, b) => a.localeCompare(b))
    .map((title) => {
      const sessions = mine.filter((a) => a.title === title);
      const latest = [...sessions].sort((a, b) => b.date.localeCompare(a.date))[0]!;
      const dots = days.map<HabitDot>((date) => {
        const s = sessions.find((a) => a.date === date);
        if (!s) return { date, state: 'none' };
        if (s.checkIn) return { date, state: s.checkIn.state };
        return { date, state: date < today ? 'missed' : 'open' };
      });
      return {
        title,
        anchor: latest.anchor ? { words: latest.anchor.words, time: latest.anchor.time } : null,
        slot: latest.slot,
        targetMinutes: latest.targetMinutes ?? null,
        reminderOn: latest.reminderOn !== false,
        dots,
        doneDays: dots.filter((d) => d.state === 'done' || d.state === 'partly').length,
        sample: latest,
      };
    });
}
