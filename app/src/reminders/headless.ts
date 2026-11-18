import type { CheckInApi } from '../today/checkin';
import type { CheckInState } from '../today/today';
import type { ReminderData } from './notifier';

/** The buttons on a reminder, by the identifier the OS hands back. */
export const REMINDER_CATEGORY = 'reminder';
export const ACTION_IDS = { done: 'done', partly: 'partly', skip: 'skip' } as const;

/** The parts of a notification response the headless task reads. `content.data` is empty with the app killed. */
export type ReminderResponse = {
  actionIdentifier: string;
  userText?: string;
  notification: { request: { identifier: string; content: { dataString?: string | null } } };
};

/** The Action a reminder is for, from `content.dataString`. Null when it is missing or not ours. */
export function parseReminderData(dataString: string | null | undefined): ReminderData | null {
  if (!dataString) return null;
  try {
    const data = JSON.parse(dataString) as { actionId?: unknown };
    return typeof data.actionId === 'string' && data.actionId ? { actionId: data.actionId } : null;
  } catch {
    return null;
  }
}

/** Minutes from what the user typed into Partly: "20", "20 min", "1.5h". Null when there is no usable number. */
export function parseMinutes(text: string | undefined): number | null {
  const m = /(\d+(?:[.,]\d+)?)\s*(h|hr|hrs|hour|hours)?\b/i.exec(text ?? '');
  if (!m) return null;
  const n = Number(m[1]!.replace(',', '.')) * (m[2] ? 60 : 1);
  const minutes = Math.round(n);
  return minutes >= 1 && minutes <= 24 * 60 ? minutes : null;
}

export type HeadlessDeps = {
  /** Writes to the outbox and tries to send (the same path as a Check-in made in the app). */
  checkIn: Pick<CheckInApi, 'put'>;
  now: () => Date;
  newId: () => string;
  dismiss: (notificationId: string) => Promise<void>;
  /** After a Check-in, the checked-in Action's reminder is not wanted again. */
  rearm: () => Promise<void>;
};

const STATES: Record<string, CheckInState> = { [ACTION_IDS.done]: 'done', [ACTION_IDS.partly]: 'partly', [ACTION_IDS.skip]: 'skipped' };

/**
 * The headless task: a tap on Done · Partly · Skip with the app killed. Writes the Check-in to the outbox, clears the
 * notification and re-arms. A body tap, or a Partly with no minutes, records nothing.
 */
export async function handleReminderResponse(res: ReminderResponse, deps: HeadlessDeps): Promise<'recorded' | 'ignored'> {
  const state = STATES[res.actionIdentifier];
  const data = parseReminderData(res.notification.request.content.dataString);
  if (!state || !data) return 'ignored';
  const minutes = state === 'partly' ? parseMinutes(res.userText) : null;
  if (state === 'partly' && minutes === null) return 'ignored';

  await deps.checkIn.put({
    scheduledActionId: data.actionId, clientId: deps.newId(), state, minutes, reasons: [], note: null, tappedAt: deps.now().toISOString(),
  });
  await deps.dismiss(res.notification.request.identifier);
  await deps.rearm();
  return 'recorded';
}
