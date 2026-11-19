import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';
import { fetch } from 'expo/fetch';
import i18n from '../i18n';
import type { PushRegistration } from './push';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

/** The channel the server's pushes name; high importance, so they always show. */
export async function setUpPushChannel() {
  if (Platform.OS !== 'android') return;
  await Notifications.setNotificationChannelAsync('coach', {
    name: i18n.t('push.channel'),
    importance: Notifications.AndroidImportance.HIGH,
  });
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
