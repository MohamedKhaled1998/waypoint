import { phoneStorage } from '../sync/phone';
import type { ScheduledActionView } from '../today/today';
import { toArm } from './arming';
import { expoNotifier } from './native';
import { arm } from './notifier';

/** Arms the next reminders from the phone's cache. Safe to call any time: app start, a sync, a fired reminder, a Check-in. */
export async function rearmReminders(now = new Date()) {
  const actions = await phoneStorage.list<ScheduledActionView>('scheduled_action');
  await arm(expoNotifier, toArm(actions, now));
}
