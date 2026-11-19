import { reminderTime } from '../reminders/arming';
import type { ScheduledActionView } from '../today/today';

/** The data the server's second silence nudge carries when reminders may not be landing. */
export function batteryHintFor(data: Record<string, unknown> | null | undefined): boolean {
  return data?.hint === 'battery_settings';
}

const LATE_MS = 2 * 60 * 60 * 1000;
const SAMPLE = 5;
const MIN_SAMPLES = 3;
const LATE_SHARE = 0.6;

/**
 * Check-in timing that suggests the phone kills Waypoint: of the last few Check-ins on Actions with a reminder, most
 * were tapped hours after the reminder should have rung, so the user found them by opening the app.
 */
export function remindersLookDead(actions: ScheduledActionView[]): boolean {
  const recent = actions
    .filter((a) => a.checkIn && a.reminderOn !== false && reminderTime(a))
    .sort((a, b) => b.checkIn!.tappedAt.localeCompare(a.checkIn!.tappedAt))
    .slice(0, SAMPLE);
  if (recent.length < MIN_SAMPLES) return false;
  const late = recent.filter((a) => new Date(a.checkIn!.tappedAt).getTime() - reminderTime(a)!.getTime() > LATE_MS).length;
  return late / recent.length >= LATE_SHARE;
}
