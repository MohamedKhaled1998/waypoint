import { Pressable, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import type { Hue } from '../theme/tokens';
import { resultLabel, type CheckInState, type ScheduledActionView, type TodayModel } from './today';

export type Aspiration = { aspirationId: string; shortName: string; hue: string };
const hueOf = (aspirations: Aspiration[], id: string): Hue | undefined => {
  const hue = aspirations.find((a) => a.aspirationId === id)?.hue;
  return hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined;
};

export function NowCard({
  now, onOpen, onQuick, onPartly,
}: {
  now: NonNullable<TodayModel['nowCard']>;
  onOpen: (a: ScheduledActionView) => void;
  onQuick: (a: ScheduledActionView, state: CheckInState) => void;
  onPartly: (a: ScheduledActionView) => void;
}) {
  const { t } = useTranslation();
  const a = now.action;
  return (
    <Card style={styles.now}>
      <Pressable accessibilityRole="button" onPress={() => onOpen(a)}>
        <Text variant="label" muted>
          {t(now.kind === 'overdue' ? 'today.overdueTitle' : 'today.nextTitle', { time: a.slotStart })}
        </Text>
        <Text variant="heading">{a.title}</Text>
      </Pressable>
      <View style={styles.row}>
        <View style={styles.flex}><Button kind="secondary" title={t('today.state.skip')} onPress={() => onQuick(a, 'skipped')} /></View>
        <View style={styles.flex}><Button kind="secondary" title={t('today.state.partly')} onPress={() => onPartly(a)} /></View>
        <View style={styles.flex}><Button title={t('today.state.done')} onPress={() => onQuick(a, 'done')} /></View>
      </View>
    </Card>
  );
}

export function TimelineRow({
  action, aspirations, isNow, onOpen,
}: { action: ScheduledActionView; aspirations: Aspiration[]; isNow: boolean; onOpen: (a: ScheduledActionView) => void }) {
  const { t } = useTranslation();
  const result = resultLabel(action);
  return (
    <View style={[styles.rowItem, result && styles.dim]}>
      <Text variant="caption" muted style={styles.time}>
        {action.slotStart}
      </Text>
      <Pressable accessibilityRole="button" style={styles.flex} onPress={() => onOpen(action)}>
        <Card hue={hueOf(aspirations, action.aspirationId)} style={[styles.actionCard, isNow && styles.outlined]}>
          {isNow ? <Text variant="caption">{t('today.nowOutlined')}</Text> : null}
          <Text variant="label">{action.title}</Text>
          <Text variant="caption" muted>
            {result ? t(`today.result.${result.state}`, result.state === 'partly' ? { minutes: result.minutes, planned: result.planned } : {}) : t('today.minutes', { count: action.minutes })}
          </Text>
        </Card>
      </Pressable>
    </View>
  );
}

export function NowLine() {
  const { t } = useTranslation();
  return (
    <View style={styles.nowLine} accessibilityLabel={t('today.nowLabel')}>
      <Text variant="caption" style={styles.nowText}>
        {t('today.nowLabel')}
      </Text>
      <View style={styles.rule} />
    </View>
  );
}

export function AspirationChips({ aspirations }: { aspirations: Aspiration[] }) {
  return (
    <View style={styles.chips}>
      {aspirations.map((a) => (
        <View key={a.aspirationId} style={styles.chip}>
          <View style={[styles.dot, hueOf(aspirations, a.aspirationId) ? styles.hueDot(hueOf(aspirations, a.aspirationId)!) : null]} />
          <Text variant="caption">{a.shortName}</Text>
        </View>
      ))}
    </View>
  );
}

export function WaitingPill({ label, onPress }: { label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.pill}>
      <Text variant="caption">{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  now: { gap: theme.space.md },
  row: { flexDirection: 'row', gap: theme.space.sm },
  flex: { flex: 1 },
  rowItem: { flexDirection: 'row', gap: theme.space.md, alignItems: 'flex-start' },
  dim: { opacity: 0.55 },
  time: { width: 44, paddingTop: theme.space.md },
  actionCard: { padding: theme.space.md, gap: theme.space.xs },
  outlined: { borderColor: theme.colors.accent, borderWidth: 2 },
  nowLine: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  nowText: { width: 44, color: theme.colors.accent },
  rule: { flex: 1, height: 2, backgroundColor: theme.colors.accent },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.xs,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.xs,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: theme.colors.textMuted },
  hueDot: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  pill: {
    flex: 1,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.accentTint,
    alignItems: 'center',
  },
}));
