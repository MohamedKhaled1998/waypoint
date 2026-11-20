import { Ionicons } from '@expo/vector-icons';
import { Tabs, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { useUnistyles } from 'react-native-unistyles';
import { useCoachBadge } from '../../coach/useCoachBadge';
import { HeaderButton } from '../../ui/HeaderButton';

export default function TabsLayout() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const router = useRouter();
  const badge = useCoachBadge();
  return (
    <Tabs
      screenOptions={{
        headerStyle: { backgroundColor: theme.colors.background },
        headerTintColor: theme.colors.text,
        headerShadowVisible: false,
        headerTitleStyle: { fontWeight: theme.type.heading.fontWeight, fontSize: theme.type.heading.fontSize },
        headerTitleAlign: 'left',
        headerRight: () => (
          <HeaderButton icon="settings-outline" label={t('settings.open')} onPress={() => router.push('/settings')} />
        ),
        tabBarActiveTintColor: theme.colors.accent,
        tabBarInactiveTintColor: theme.colors.textMuted,
        tabBarStyle: { backgroundColor: theme.colors.surface, borderTopColor: theme.colors.border, borderTopWidth: 1 },
        tabBarLabelStyle: { fontWeight: '600' },
        sceneStyle: { backgroundColor: theme.colors.background },
      }}
    >
      <Tabs.Screen
        name="index"
        options={{
          title: t('tabs.today'),
          tabBarIcon: ({ focused, size }) => <Ionicons name={focused ? 'sunny' : 'sunny-outline'} color={focused ? theme.colors.accent : theme.colors.textMuted} size={size} />,
        }}
      />
      <Tabs.Screen
        name="progress"
        options={{
          title: t('tabs.progress'),
          tabBarIcon: ({ focused, size }) => <Ionicons name={focused ? 'trail-sign' : 'trail-sign-outline'} color={focused ? theme.colors.accent : theme.colors.textMuted} size={size} />,
        }}
      />
      <Tabs.Screen
        name="coach"
        options={{
          title: t('tabs.coach'),
          tabBarBadge: badge,
          tabBarBadgeStyle: { backgroundColor: theme.colors.accent, color: theme.colors.onAccent },
          tabBarIcon: ({ focused, size }) => <Ionicons name={focused ? 'chatbubbles' : 'chatbubbles-outline'} color={focused ? theme.colors.accent : theme.colors.textMuted} size={size} />,
        }}
      />
    </Tabs>
  );
}
