import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { stepCounter, type FirstRunStep } from '../firstrun/firstRun';
import { Text } from './Text';

const NAMES = ['signIn', 'aspiration', 'interview', 'firstPlan'] as const;

/** Four segment bars and "Step n of 4 · name", on every first-run screen. */
export function StepCounter({ step }: { step: FirstRunStep }) {
  const { t } = useTranslation();
  const c = stepCounter(step);
  if (!c) return null;
  return (
    <View style={styles.counter} accessibilityLabel={t('firstRun.stepNamed', { n: c.n, of: c.of, name: t(`firstRun.stepNames.${NAMES[c.n - 1]}`) })}>
      <View style={styles.bars}>
        {NAMES.map((name, i) => (
          <View key={name} style={[styles.bar, i < c.n && styles.barOn]} />
        ))}
      </View>
      <Text variant="label" muted style={styles.regular}>{t('firstRun.stepNamed', { n: c.n, of: c.of, name: t(`firstRun.stepNames.${NAMES[c.n - 1]}`) })}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  counter: { gap: 6, paddingVertical: theme.space.xs },
  bars: { flexDirection: 'row', gap: theme.space.xs },
  bar: { flex: 1, height: 4, borderRadius: 2, backgroundColor: theme.colors.surfaceAlt },
  barOn: { backgroundColor: theme.colors.accent },
  regular: { fontWeight: '400' },
}));
