import '../theme/unistyles';
import '../i18n';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { useTranslation } from 'react-i18next';
import { useUnistyles } from 'react-native-unistyles';
import { SessionProvider, useSession } from '../auth/SessionProvider';
import { View } from 'react-native';

function Routes() {
  const { status } = useSession();
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

