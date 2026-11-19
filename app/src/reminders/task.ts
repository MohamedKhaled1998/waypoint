import * as Notifications from 'expo-notifications';
import * as TaskManager from 'expo-task-manager';
import { httpAuth } from '../auth/api';
import { keystoreStorage } from '../auth/secureStorage';
import { createSession } from '../auth/session';
import { createSync } from '../sync/engine';
import { httpRemote } from '../sync/remote';
import { phoneStorage } from '../sync/phone';
import { createOfflineToday } from '../today/offline';
import { handleReminderResponse, type ReminderResponse } from './headless';
import { expoNotifier } from './native';
import { acceptRefresh } from '../push/native';
import { handleRefreshResponse, type RefreshResponse } from '../push/push';
import { rearmReminders } from './rearm';

export const REMINDER_TASK = 'waypoint-reminder-response';

/** Runs with the app killed: Done · Partly · Skip from the shade or lock screen. */
TaskManager.defineTask<Notifications.NotificationTaskPayload>(REMINDER_TASK, async ({ data, error }) => {
  if (error || !data || !('actionIdentifier' in data)) return;
  const session = createSession(keystoreStorage, httpAuth);
  // "Looks good" on the weekly refresh prompt shares this task: it answers its own button and leaves the rest.
  const auth = { getAccessToken: session.getAccessToken, renewAccess: () => session.restore().catch(() => false) };
  const refresh = await handleRefreshResponse(data as unknown as RefreshResponse, {
    accept: (id) => acceptRefresh(auth, id),
    dismiss: (id) => expoNotifier.dismiss(id),
  });
  if (refresh !== 'ignored') return;
  const sync = createSync({
    storage: phoneStorage,
    remote: httpRemote({ getAccessToken: session.getAccessToken, renewAccess: () => session.restore().catch(() => false) }),
    now: () => new Date(),
    newId: () => `c-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
  });
  await handleReminderResponse(data as unknown as ReminderResponse, {
    checkIn: createOfflineToday({ storage: phoneStorage, sync }),
    now: () => new Date(),
    newId: () => `ci-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
    dismiss: (id) => expoNotifier.dismiss(id),
    rearm: () => rearmReminders(),
  });
  // The send runs behind the enqueue; give it the task's time to land, and re-arm either way.
  await sync.sync();
});

export function registerReminderTask() {
  return Notifications.registerTaskAsync(REMINDER_TASK).catch(() => {});
}
