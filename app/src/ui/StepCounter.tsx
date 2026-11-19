import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { stepCounter, type FirstRunStep } from '../firstrun/firstRun';
import { Text } from './Text';

/** "Step n of 4", on every first-run screen. */
export function StepCounter({ step }: { step: FirstRunStep }) {
  const { t } = useTranslation();
  const c = stepCounter(step);
  if (!c) return null;
  return (
    <Text variant="caption" muted style={styles.counter}>
      {t('firstRun.step', { n: c.n, of: c.of })}
    </Text>
  );
}

const styles = StyleSheet.create((theme) => ({
  counter: { paddingVertical: theme.space.xs },
}));
