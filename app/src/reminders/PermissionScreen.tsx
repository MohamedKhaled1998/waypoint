import Ionicons from '@expo/vector-icons/Ionicons';
import { useSyncExternalStore } from 'react';
import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';
import type { PermissionStep } from './permissions';

type Pending = { step: PermissionStep; resolve: (yes: boolean) => void } | null;

let pending: Pending = null;
const listeners = new Set<() => void>();
const set = (next: Pending) => {
  pending = next;
  listeners.forEach((fn) => fn());
};

/** Shows the full-screen reason for one permission; resolves true when the user taps Allow. */
export function explainOnScreen(step: PermissionStep): Promise<boolean> {
  return new Promise((resolve) => set({ step, resolve }));
}

const ORDER: PermissionStep[] = ['notifications', 'exactAlarms'];
const ICONS: Record<PermissionStep, keyof typeof Ionicons.glyphMap> = { notifications: 'notifications-outline', exactAlarms: 'alarm-outline' };

/** Mounted once at the root: one permission at a time, each with its reason and what happens without it. */
export function PermissionScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const current = useSyncExternalStore(
    (fn) => {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    () => pending,
  );
  if (!current) return null;
  const answer = (yes: boolean) => {
    set(null);
    current.resolve(yes);
  };
  const { step } = current;
  return (
    <View style={styles.screen} accessibilityViewIsModal>
      <View style={styles.top}>
        <StepCounter step="permissions" />
        <Text variant="caption" muted>{t('reminders.permission.counter', { n: ORDER.indexOf(step) + 1, of: ORDER.length })}</Text>
        <View style={styles.tile}>
          <Ionicons name={ICONS[step]} size={32} color={theme.colors.accent} />
        </View>
        <Text variant="title">{t(`reminders.permission.${step}.title`)}</Text>
        <Text>{t(`reminders.permission.${step}.why`)}</Text>
        <Text variant="label" muted style={styles.regular}>{t(`reminders.permission.${step}.without`)}</Text>
      </View>
      <View style={styles.buttons}>
        <Button title={t('reminders.permission.allow')} onPress={() => answer(true)} />
        <Button kind="quiet" title={t('reminders.permission.notNow')} onPress={() => answer(false)} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  screen: {
    ...StyleSheet.absoluteFillObject,
    justifyContent: 'space-between',
    padding: theme.space.xl,
    paddingTop: 32 + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  top: { gap: theme.space.lg },
  tile: { width: 64, height: 64, borderRadius: 20, alignItems: 'center', justifyContent: 'center', backgroundColor: theme.colors.accentTint },
  regular: { fontWeight: '400' },
  buttons: { gap: theme.space.sm },
}));
