import i18n from '../i18n';
import { phoneStorage } from '../sync/phone';
import type { ScheduledActionView } from '../today/today';
import { toArm } from './arming';
import { upcomingFollowUps } from './followUp';
import { expoNotifier } from './native';
import { arm } from './notifier';

let lastInteractedAt: Date | null = null;

/** The user opened the app or tapped a button: today's Follow-ups are not sent. */
export function noteInteraction(at = new Date()) {
  lastInteractedAt = at;
}

/** Arms the next reminders and Follow-ups from the phone's cache. Safe to call any time: app start, a sync, a fired reminder, a Check-in. */
export async function rearmReminders(now = new Date()) {
  const actions = await phoneStorage.list<ScheduledActionView>('scheduled_action');
  // No Pause on the phone yet, so none is in force.
  const followUps = upcomingFollowUps(actions, { now, interactedAt: lastInteractedAt, paused: false }).map((f) => ({
    groupKey: `follow-up-${f.actionId}`, at: f.at, title: f.title, body: i18n.t(`reminders.followUp.${f.tone}`), data: { actionId: f.actionId }, sticky: false,
  }));
  await arm(expoNotifier, toArm(actions, now), followUps);
}
