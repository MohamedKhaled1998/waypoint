import * as Notifications from 'expo-notifications';
import { Linking, Platform } from 'react-native';
import { fetch } from 'expo/fetch';
import i18n from '../i18n';
import { REFRESH_CATEGORY, REFRESH_LOOKS_GOOD, type PushRegistration } from './push';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

/** The channel the server's pushes name; high importance, so they always show. */
export async function setUpPushChannel() {
  if (Platform.OS !== 'android') return;
  await Notifications.setNotificationChannelAsync('coach', {
    name: i18n.t('push.channel'),
    importance: Notifications.AndroidImportance.HIGH,
  });
  // "Looks good" on the weekly refresh prompt, answered from the shade without opening the app.
  await Notifications.setNotificationCategoryAsync(REFRESH_CATEGORY, [
    { identifier: REFRESH_LOOKS_GOOD, buttonTitle: i18n.t('push.looksGood'), options: { opensAppToForeground: false } },
  ]);
  // A push that arrives while the app is open still shows.
  Notifications.setNotificationHandler({
    handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: false, shouldSetBadge: false }),
  });
}

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

async function call(auth: Auth, method: 'PUT' | 'DELETE', body?: unknown) {
  const go = () =>
    fetch(`${baseUrl}/me/push-token`, {
      method,
      body: body === undefined ? undefined : JSON.stringify(body),
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
    });
  let res = await go();
  if (res.status === 401 && (await auth.renewAccess())) res = await go();
  if (!res.ok) throw new Error(`push token ${res.status}`);
}

export const phonePush = (auth: Auth): PushRegistration => ({
  // The token needs no notification permission; showing a push does (asked after the first Plan, see reminders).
  getToken: async () => (await Notifications.getDevicePushTokenAsync()).data as string,
  onRefresh: (cb) => {
    const sub = Notifications.addPushTokenListener((t) => cb(t.data as string));
    return () => sub.remove();
  },
  send: (token) => call(auth, 'PUT', { token }),
});

/** Best effort on sign-out, so the next user on this phone never gets this user's pushes. */
export const forgetPushToken = (auth: Auth) => call(auth, 'DELETE').catch(() => {});

/** Accepts a weekly refresh on the server ("Looks good"). Throws when it cannot, so the notification stays. */
export async function acceptRefresh(auth: Auth, refreshId: string) {
  const go = () =>
    fetch(`${baseUrl}/weekly-refresh/${encodeURIComponent(refreshId)}/accept`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
    });
  let res = await go();
  if (res.status === 401 && (await auth.renewAccess())) res = await go();
  if (!res.ok) throw new Error(`weekly refresh ${res.status}`);
}

/** Deep-links to the device's battery settings, where the user can let Waypoint run in the background. */
export async function openBatterySettings() {
  try {
    await Linking.sendIntent('android.settings.IGNORE_BATTERY_OPTIMIZATION_SETTINGS');
  } catch {
    await Linking.openSettings();
  }
}
