import { explainOnScreen } from './PermissionScreen';
import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';
import i18n from '../i18n';
import { ACTION_IDS, REMINDER_CATEGORY } from './headless';
import type { Notifier } from './notifier';
import { keystoreStorage } from '../auth/secureStorage';
import type { AskedFlag, PermissionPlatform, PermissionStep } from './permissions';

const CHANNEL = 'reminders';

/** The one-time set-up: the channel, and the buttons every reminder carries. */
export async function setUpReminders() {
  if (Platform.OS !== 'android') return;
  await Notifications.setNotificationChannelAsync(CHANNEL, {
    name: i18n.t('reminders.channel'),
    importance: Notifications.AndroidImportance.HIGH,
    bypassDnd: false,
  });
  await Notifications.setNotificationCategoryAsync(REMINDER_CATEGORY, [
    { identifier: ACTION_IDS.done, buttonTitle: i18n.t('reminders.done'), options: { opensAppToForeground: false } },
    {
      identifier: ACTION_IDS.partly,
      buttonTitle: i18n.t('reminders.partly'),
      textInput: { submitButtonTitle: i18n.t('reminders.partlySubmit'), placeholder: i18n.t('reminders.partlyPlaceholder') },
      options: { opensAppToForeground: false },
    },
    { identifier: ACTION_IDS.skip, buttonTitle: i18n.t('reminders.skip'), options: { opensAppToForeground: false } },
  ]);
}

/** Local alarms through expo-notifications: exact when "Alarms & reminders" is granted, else inexact; the library picks. */
export const expoNotifier: Notifier = {
  cancelAll: () => Notifications.cancelAllScheduledNotificationsAsync(),
  async schedule(r) {
    await Notifications.scheduleNotificationAsync({
      content: {
        title: r.title,
        // No text yet: the title alone. The Coach is late, never faked.
        ...(r.body ? { body: r.body } : {}),
        data: r.data,
        categoryIdentifier: REMINDER_CATEGORY,
        threadIdentifier: r.groupKey,
        ...(r.sticky ? { sticky: true, autoDismiss: false } : {}),
      },
      trigger: { type: Notifications.SchedulableTriggerInputTypes.DATE, date: r.at, channelId: CHANNEL },
    });
  },
  dismiss: (id) => Notifications.dismissNotificationAsync(id),
};

const ASKED_KEY = 'reminder-permissions-asked';

/** The one-time flag, in the Keystore-backed store. */
export const askedFlag: AskedFlag = {
  get: async () => (await keystoreStorage.get(ASKED_KEY)) === '1',
  set: () => keystoreStorage.set(ASKED_KEY, '1'),
};

/** The OS side of the permission steps, with a plain dialog that says why before each one. */
export const phonePermissions: PermissionPlatform = {
  async granted(step: PermissionStep) {
    const res = step === 'notifications' ? await Notifications.getPermissionsAsync() : await Notifications.getExactAlarmPermissionsAsync();
    return res.granted;
  },
  async request(step: PermissionStep) {
    if (step === 'notifications') await Notifications.requestPermissionsAsync();
    else await Notifications.requestExactAlarmPermissionsAsync().catch(() => {});
  },
  explain(step: PermissionStep) {
    return explainOnScreen(step);
  },
};
