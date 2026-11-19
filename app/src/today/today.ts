export type CheckInState = 'done' | 'partly' | 'skipped';

export type CheckInView = {
  state: CheckInState;
  minutes: number | null;
  reasons: string[];
  note: string | null;
  tappedAt: string;
};

export type CheckInRequest = {
  scheduledActionId: string;
  clientId: string;
  state: CheckInState;
  minutes: number | null;
  reasons: string[];
  note: string | null;
  tappedAt: string;
};

export type ScheduledActionView = {
  id: string;
  aspirationId: string;
  date: string;
  slotId?: string | null;
  slot: string | null;
  slotStart: string | null;
  slotEnd: string | null;
  title: string;
  /** habit: recurring; task: one-off; catch_up: extra time after a shortfall. */
  /** The Aspiration's reaction to a skip, which words the Follow-up. */
  onSkip?: 'gentle_check' | 'quiet_replan' | 'ask_why';
  kind?: 'habit' | 'task' | 'catch_up';
  minutes: number;
  checkIn: CheckInView | null;
  /** now: at the Slot's start; get_ready: once, `reminderLeadMinutes` ahead. */
  reminderKind?: 'now' | 'get_ready';
  reminderLeadMinutes?: number | null;
  /** Written ahead by the server, so a reminder needs no model call. Null until it arrives. */
  reminderText?: string | null;
  reminderOn?: boolean;
};

export type TodayModel = {
  state: 'none' | 'rest' | 'active' | 'allCheckedIn';
  timeline: ScheduledActionView[];
  /** How many timeline items come before the "now" line. */
  nowLineBefore: number;
  nowCard: { kind: 'overdue' | 'next'; action: ScheduledActionView } | null;
  summary: { checkedIn: number; total: number; minutesDone: number; minutesPlanned: number };
  tomorrowFirst: ScheduledActionView | null;
  /** The last date the cache has Actions for; nothing is planned past it until the phone connects. */
  plannedUntil: string | null;
};

const startOf = (a: ScheduledActionView) => a.slotStart ?? '00:00';
const byTime = (a: ScheduledActionView, b: ScheduledActionView) => startOf(a).localeCompare(startOf(b)) || a.title.localeCompare(b.title);

/** Today's view of the scheduled Actions at a local date and time ("YYYY-MM-DD", "HH:mm"). */
export function buildToday(actions: ScheduledActionView[], now: { date: string; time: string }): TodayModel {
  const timeline = actions.filter((a) => a.date === now.date).sort(byTime);
  const open = timeline.filter((a) => !a.checkIn);
  const overdue = open.find((a) => startOf(a) < now.time);
  const next = open.find((a) => startOf(a) >= now.time);
  const nowCard = overdue ? { kind: 'overdue' as const, action: overdue } : next ? { kind: 'next' as const, action: next } : null;
  const minutesDone = timeline.reduce((sum, a) => {
    if (a.checkIn?.state === 'done') return sum + a.minutes;
    if (a.checkIn?.state === 'partly') return sum + (a.checkIn.minutes ?? 0);
    return sum;
  }, 0);
  const upcoming = actions.filter((a) => a.date > now.date).sort((a, b) => a.date.localeCompare(b.date) || byTime(a, b));
  const tomorrowDate = upcoming[0]?.date;
  const state: TodayModel['state'] =
    timeline.length === 0 ? (actions.length === 0 ? 'none' : 'rest') : open.length === 0 ? 'allCheckedIn' : 'active';
  return {
    state,
    timeline,
    nowLineBefore: timeline.filter((a) => startOf(a) < now.time).length,
    nowCard,
    summary: {
      checkedIn: timeline.length - open.length,
      total: timeline.length,
      minutesDone,
      minutesPlanned: timeline.reduce((sum, a) => sum + a.minutes, 0),
    },
    tomorrowFirst: upcoming.find((a) => a.date === tomorrowDate) ?? null,
    plannedUntil: actions.reduce<string | null>((last, a) => (last === null || a.date > last ? a.date : last), null),
  };
}

export type ResultLabel = { state: 'done' } | { state: 'skipped' } | { state: 'partly'; minutes: number; planned: number };

export function resultLabel(a: ScheduledActionView): ResultLabel | null {
  if (!a.checkIn) return null;
  if (a.checkIn.state === 'partly') return { state: 'partly', minutes: a.checkIn.minutes ?? 0, planned: a.minutes };
  return { state: a.checkIn.state };
}
