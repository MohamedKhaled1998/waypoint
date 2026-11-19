import '../theme/unistyles';
import '../i18n';
import { Stack, useRouter } from 'expo-router';
import * as Notifications from 'expo-notifications';
import { useEffect, useRef } from 'react';
import '../reminders/task';
import { setUpReminders } from '../reminders/native';
import { rearmReminders } from '../reminders/rearm';
import { registerReminderTask } from '../reminders/task';
import { StatusBar } from 'expo-status-bar';
import { useTranslation } from 'react-i18next';
import { useUnistyles } from 'react-native-unistyles';
import { phonePush, setUpPushChannel } from '../push/native';
import { batteryHintFor } from '../pause/battery';
import { registerPushToken, routeForPush } from '../push/push';
import { SessionProvider, useSession } from '../auth/SessionProvider';
import { View } from 'react-native';

function Routes() {
  const { status, phone, getAccessToken, renewAccess } = useSession();
  const router = useRouter();

  // Arm the next reminders on app start, whenever the cache changes (a pull, a Check-in) and whenever one fires.
  useEffect(() => {
    if (status !== 'signedIn') return;
    const rearm = () => void rearmReminders().catch(() => console.warn('reminders: arming failed'));
    void setUpReminders().then(registerReminderTask).then(rearm).catch(() => console.warn('reminders: arming failed'));
    const unsubscribe = phone.subscribe(rearm);
    const fired = Notifications.addNotificationReceivedListener(rearm);
    return () => {
      unsubscribe();
      fired.remove();
    };
  }, [status, phone]);

  // The phone's FCM token goes to the API after sign-in and whenever it refreshes.
  useEffect(() => {
    if (status !== 'signedIn') return;
    void setUpPushChannel().catch(() => console.warn('push: channel failed'));
    const registration = registerPushToken(phonePush({ getAccessToken, renewAccess }));
    return registration.stop;
  }, [status, getAccessToken, renewAccess]);

  // A tap on the reminder itself opens Today with that Action's sheet. The hook also returns the tap that started the
  // app, and is mounted before sign-in, so a cold start is not missed; the tap waits for the signed-in routes.
  const lastResponse = Notifications.useLastNotificationResponse();
  const handled = useRef<string | null>(null);
  useEffect(() => {
    if (status !== 'signedIn' || !lastResponse) return;
    if (lastResponse.actionIdentifier !== Notifications.DEFAULT_ACTION_IDENTIFIER) return;
    const id = lastResponse.notification.request.identifier + ':' + lastResponse.notification.date;
    if (handled.current === id) return;
    handled.current = id;
    const data = lastResponse.notification.request.content.data as { actionId?: string } | null;
    // A push carries a route; a reminder carries its Action.
    // The second silence nudge hints at the battery setting: Today offers it.
    if (batteryHintFor(data)) {
      router.navigate({ pathname: '/', params: { battery: '1' } });
      return;
    }
    const pushRoute = routeForPush(data);
    if (pushRoute) {
      router.navigate(pushRoute);
      return;
    }
    const actionId = data?.actionId;
    if (actionId) router.navigate({ pathname: '/', params: { action: actionId } });
  }, [status, lastResponse, router]);
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  if (status === 'loading') return <View style={{ flex: 1, backgroundColor: theme.colors.background }} />;
  return (
    <Stack
      screenOptions={{
        headerStyle: { backgroundColor: theme.colors.background },
        headerTintColor: theme.colors.text,
        headerShadowVisible: false,
        contentStyle: { backgroundColor: theme.colors.background },
      }}
    >
      <Stack.Protected guard={status === 'signedIn'}>
        <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
        <Stack.Screen name="interview" options={{ title: t('interview.title') }} />
        <Stack.Screen name="plan-review" options={{ title: t('plan.title') }} />
        <Stack.Screen name="aspiration" options={{ title: '' }} />
        <Stack.Screen name="settings" options={{ title: t('settings.title') }} />
      </Stack.Protected>
      <Stack.Protected guard={status === 'signedOut'}>
        <Stack.Screen name="sign-in" options={{ headerShown: false }} />
      </Stack.Protected>
    </Stack>
  );
}

export default function RootLayout() {
  const { theme } = useUnistyles();
  return (
    <SessionProvider>
      <StatusBar style={theme.isDark ? 'light' : 'dark'} />
      <Routes />
    </SessionProvider>
  );
}

