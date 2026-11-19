import type { ScheduledActionView } from '../today/today';

export type SkipTone = 'gentle_check' | 'quiet_replan' | 'ask_why';

/** What the phone knows when deciding. `planning` decides ahead of the Slot's end, so only Actions already past count as unreported.
 *  `interactedAt` is the user's last tap or app open; `paused` is a Pause in force. */
export type FollowUpContext = { now: Date; interactedAt: Date | null; paused: boolean; tone?: SkipTone; planning?: boolean };

export type FollowUpDecision =
  | { send: true; at: Date; actionId: string; tone: SkipTone }
  | { send: false; reason: 'reminder_off' | 'no_slot_end' | 'checked_in' | 'not_yet' | 'paused' | 'interacted_today' | 'habit_can_still_happen' | 'last_two_unreported' };

const parseDate = (d: string) => {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(d);
  return m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : null;
};

/** The local date-time of an Action's Slot end. */
function slotEnd(a: ScheduledActionView): Date | null {
  const day = parseDate(a.date);
  const time = a.slotEnd ? /^(\d{2}):(\d{2})/.exec(a.slotEnd) : null;
  return day && time ? new Date(day.getFullYear(), day.getMonth(), day.getDate(), Number(time[1]), Number(time[2])) : null;
}

const sameDay = (x: Date, y: Date) => x.getFullYear() === y.getFullYear() && x.getMonth() === y.getMonth() && x.getDate() === y.getDate();

/** Monday of the week a date falls in, as a day number. */
const weekOf = (d: Date) => Math.floor((Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()) / 86_400_000 + 3) / 7);

/**
 * At most one Follow-up, at the end of the Slot, with the same buttons as the reminder. Not sent when the user
 * interacted that day, a Pause is on, the Action is a habit that can still happen later that week, or the last two
 * Actions went unreported. Anchor Actions (no Slot end) get none.
 */
export function followUp(a: ScheduledActionView, all: ScheduledActionView[], ctx: FollowUpContext): FollowUpDecision {
  if (a.reminderOn === false) return { send: false, reason: 'reminder_off' };
  const at = slotEnd(a);
  if (!at) return { send: false, reason: 'no_slot_end' };
  if (a.checkIn) return { send: false, reason: 'checked_in' };
  if (!ctx.planning && ctx.now.getTime() < at.getTime()) return { send: false, reason: 'not_yet' };
  if (ctx.paused) return { send: false, reason: 'paused' };
  if (ctx.interactedAt && sameDay(ctx.interactedAt, at)) return { send: false, reason: 'interacted_today' };

  const day = parseDate(a.date)!;
  if (a.kind === 'habit' && all.some((o) => {
    const od = parseDate(o.date);
    return o.id !== a.id && o.title === a.title && o.kind === 'habit' && !o.checkIn && od !== null && od > day && weekOf(od) === weekOf(day);
  })) return { send: false, reason: 'habit_can_still_happen' };

  const previous = all.filter((o) => o.id !== a.id && (slotEnd(o)?.getTime() ?? Infinity) < Math.min(at.getTime(), ctx.planning ? ctx.now.getTime() : Infinity))
    .sort((x, y) => slotEnd(y)!.getTime() - slotEnd(x)!.getTime()).slice(0, 2);
  if (previous.length === 2 && previous.every((o) => !o.checkIn)) return { send: false, reason: 'last_two_unreported' };

  return { send: true, at, actionId: a.id, tone: ctx.tone ?? 'gentle_check' };
}

export type PlannedFollowUp = { actionId: string; at: Date; tone: SkipTone; title: string };

/**
 * The Follow-ups to have armed now: each Action's own decision, taken ahead of its Slot's end and in the Aspiration's
 * skip tone. Re-armed after every interaction, so a tap today cancels today's.
 */
export function upcomingFollowUps(actions: ScheduledActionView[], ctx: Omit<FollowUpContext, 'tone' | 'planning'>): PlannedFollowUp[] {
  const out: PlannedFollowUp[] = [];
  for (const a of actions) {
    const d = followUp(a, actions, { ...ctx, tone: a.onSkip, planning: true });
    if (d.send && d.at.getTime() > ctx.now.getTime()) out.push({ actionId: a.id, at: d.at, tone: d.tone, title: a.title });
  }
  return out.sort((x, y) => x.at.getTime() - y.at.getTime() || x.actionId.localeCompare(y.actionId));
}
