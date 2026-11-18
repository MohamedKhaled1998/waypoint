import type { ScheduledActionView } from '../today/today';

/** One reminder: what rings, for which Action, and when. */
export type Reminder = { actionId: string; at: Date; title: string; text: string | null; aspirationId: string };

/** Reminders due at the same moment: one notification group with a child per Action. */
export type ReminderGroup = { key: string; at: Date; reminders: Reminder[] };

/**
 * How many upcoming moments stay armed. The phone can be dead for days, and nothing re-arms while the app is killed
 * (the notification task only runs on a tap on a button), so a wide window stays armed: one alarm per reminder at its
 * own time, which Android makes exact when "Alarms & reminders" is granted and an inexact window close to it
 * otherwise. Still well under the OS limits on scheduled alarms. Re-armed on app start, after a sync and on each
 * delivery or Check-in while the app is alive, so changes are picked up.
 */
export const MOMENTS_AHEAD = 40;
export const DEFAULT_LEAD_MINUTES = 60;

const minuteOf = (d: Date) => Math.floor(d.getTime() / 60_000);

/** The local date-time of the Action's Slot start. Null when it has no Slot time. */
export function slotStart(a: ScheduledActionView): Date | null {
  const date = /^(\d{4})-(\d{2})-(\d{2})$/.exec(a.date);
  const time = a.slotStart ? /^(\d{2}):(\d{2})/.exec(a.slotStart) : null;
  if (!date || !time) return null;
  return new Date(Number(date[1]), Number(date[2]) - 1, Number(date[3]), Number(time[1]), Number(time[2]));
}

/** When the Action's one reminder rings: the Slot's start for "now", a lead ahead for "get-ready". */
export function reminderTime(a: ScheduledActionView): Date | null {
  const start = slotStart(a);
  if (!start) return null;
  if (a.reminderKind !== 'get_ready') return start;
  return new Date(start.getTime() - (a.reminderLeadMinutes ?? DEFAULT_LEAD_MINUTES) * 60_000);
}

/**
 * Every reminder still to come, in time order: one per Action, none for an Action that is checked in, switched off,
 * or already past. Never a second ping at the start of a get-ready Action.
 */
export function upcomingReminders(actions: ScheduledActionView[], now: Date): Reminder[] {
  const out: Reminder[] = [];
  for (const a of actions) {
    if (a.reminderOn === false || a.checkIn) continue;
    const at = reminderTime(a);
    if (!at || at.getTime() <= now.getTime()) continue;
    out.push({ actionId: a.id, at, title: a.title, text: a.reminderText ?? null, aspirationId: a.aspirationId });
  }
  return out.sort((x, y) => x.at.getTime() - y.at.getTime() || x.actionId.localeCompare(y.actionId));
}

/** Reminders due in the same minute share a group. */
export function groupByMoment(reminders: Reminder[]): ReminderGroup[] {
  const groups = new Map<number, ReminderGroup>();
  for (const r of reminders) {
    const m = minuteOf(r.at);
    const g = groups.get(m) ?? { key: `moment-${m}`, at: r.at, reminders: [] };
    g.reminders.push(r);
    groups.set(m, g);
  }
  return [...groups.values()].sort((a, b) => a.at.getTime() - b.at.getTime());
}

/** What to have armed right now: the next MOMENTS_AHEAD moments, so a dead app still rings the ones after. */
export function toArm(actions: ScheduledActionView[], now: Date, ahead = MOMENTS_AHEAD): ReminderGroup[] {
  return groupByMoment(upcomingReminders(actions, now)).slice(0, ahead);
}
