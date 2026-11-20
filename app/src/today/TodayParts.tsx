import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { TourTarget } from '../firstrun/Tour';
import { Card } from '../ui/Card';
import { Pill } from '../ui/Pill';
import { Text } from '../ui/Text';
import type { Hue } from '../theme/tokens';
import { resultLabel, type CheckInState, type ScheduledActionView, type TodayModel } from './today';

export type Aspiration = { aspirationId: string; shortName: string; hue: string };
const hueOf = (aspirations: Aspiration[], id: string): Hue | undefined => {
  const hue = aspirations.find((a) => a.aspirationId === id)?.hue;
  return hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined;
};

const startOf = (a: ScheduledActionView) => (a.anchor ? `~${a.slotStart ?? a.anchor.time}` : (a.slotStart ?? ''));

/** A round quick Check-in button: tinted, or filled for Done. */
function QuickButton({ icon, color, filled, label, onPress }: { icon: keyof typeof Ionicons.glyphMap; color: string; filled?: boolean; label: string; onPress: () => void }) {
  const { theme } = useUnistyles();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      hitSlop={6}
      style={({ pressed }) => [styles.quick, { backgroundColor: filled ? color : `${color}22` }, pressed && styles.pressed]}
    >
      <Ionicons name={icon} size={18} color={filled ? theme.colors.onAccent : color} />
    </Pressable>
  );
}

/** What needs the user now: one slim hue card with Skip · Partly · Done. */
export function NowCard({
  now, aspirations, onOpen, onQuick, onPartly,
}: {
  now: NonNullable<TodayModel['nowCard']>;
  aspirations: Aspiration[];
  onOpen: (a: ScheduledActionView) => void;
  onQuick: (a: ScheduledActionView, state: CheckInState) => void;
  onPartly: (a: ScheduledActionView) => void;
}) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const a = now.action;
  const hue = hueOf(aspirations, a.aspirationId);
  const hueColor = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
  return (
    <Card hue={hue} onPress={() => onOpen(a)} style={styles.now}>
      <View style={styles.flexGap}>
        <Text variant="caption" style={{ color: hueColor }}>
          {t(now.kind === 'overdue' ? 'today.overdueTitle' : 'today.nextTitle', { time: startOf(a) })}
        </Text>
        <Text style={styles.bold} numberOfLines={1}>{a.title}</Text>
      </View>
      <TourTarget style={styles.quicks}>
        <QuickButton icon="play-skip-forward" color={theme.colors.skipped} label={t('today.state.skip')} onPress={() => onQuick(a, 'skipped')} />
        <QuickButton icon="contrast" color={theme.colors.partly} label={t('today.state.partly')} onPress={() => onPartly(a)} />
        <QuickButton icon="checkmark" color={theme.colors.done} filled label={t('today.state.done')} onPress={() => onQuick(a, 'done')} />
      </TourTarget>
    </Card>
  );
}

/** A timeline row: the time column, then the Action's hue card with inline quick buttons or its result. */
export function TimelineRow({
  action, aspirations, isNow, overdue, onOpen, onQuick,
}: {
  action: ScheduledActionView;
  aspirations: Aspiration[];
  isNow: boolean;
  overdue: boolean;
  onOpen: (a: ScheduledActionView) => void;
  onQuick: (a: ScheduledActionView, state: CheckInState) => void;
}) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const result = resultLabel(action);
  const hue = hueOf(aspirations, action.aspirationId);
  const hueColor = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
  const name = aspirations.find((x) => x.aspirationId === action.aspirationId)?.shortName;
  const resultColor = result ? { done: theme.colors.done, partly: theme.colors.partly, skipped: theme.colors.skipped }[result.state] : undefined;
  return (
    <View style={styles.rowItem}>
      <View style={styles.timeCol}>
        <Text variant="label">{startOf(action)}</Text>
        <View style={styles.timeSub}>
          {action.anchor ? <Ionicons name="repeat" size={11} color={theme.colors.textMuted} /> : null}
          <Text variant="caption" muted numberOfLines={2} style={styles.shrink}>
            {action.anchor ? action.anchor.words : t('today.minutes', { count: action.minutes })}
          </Text>
        </View>
      </View>
      <View style={styles.flex}>
        <Card hue={hue} onPress={() => onOpen(action)} style={[styles.actionCard, result && styles.dim, isNow && styles.outlined]}>
          <View style={styles.cardRow}>
            <View style={styles.flexGap}>
              <Text style={[styles.semibold, result?.state === 'done' && styles.struck]}>{action.title}</Text>
              <View style={styles.meta}>
                {name ? <Text variant="caption" style={{ color: hueColor }}>{name}</Text> : null}
                {action.guide && !result ? <Ionicons name="book-outline" size={11} color={theme.colors.textMuted} /> : null}
              </View>
              {overdue && !isNow && !result ? <Text variant="caption" style={styles.warn}>{t('today.howDidItGo')}</Text> : null}
              {isNow ? <Text variant="caption" style={styles.accent}>{t('today.nowOutlined')}</Text> : null}
            </View>
            {result ? (
              <Pill
                label={t(`today.result.${result.state}`, result.state === 'partly' ? { minutes: result.minutes, planned: result.planned } : {})}
                color={resultColor}
              />
            ) : (
              <View style={styles.quicks}>
                <QuickButton icon="play-skip-forward" color={theme.colors.skipped} label={t('today.state.skip')} onPress={() => onQuick(action, 'skipped')} />
                <QuickButton icon="checkmark" color={theme.colors.done} filled label={t('today.state.done')} onPress={() => onQuick(action, 'done')} />
              </View>
            )}
          </View>
        </Card>
      </View>
    </View>
  );
}

/** The clock time in accent, a dot and a soft rule. */
export function NowLine({ time }: { time: string }) {
  const { t } = useTranslation();
  return (
    <View style={styles.nowLine} accessibilityLabel={t('today.nowLabel')}>
      <Text variant="caption" style={[styles.nowText, styles.accent]}>{time}</Text>
      <View style={styles.nowDot} />
      <View style={styles.rule} />
    </View>
  );
}

/** One filled chip per Aspiration with what is checked in of today's Actions. */
export function AspirationChips({ aspirations, actions, onOpen }: { aspirations: Aspiration[]; actions: ScheduledActionView[]; onOpen?: (a: Aspiration) => void }) {
  const { theme } = useUnistyles();
  return (
    <View style={styles.chips}>
      {aspirations.map((a) => {
        const hue = hueOf(aspirations, a.aspirationId);
        const color = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
        const mine = actions.filter((x) => x.aspirationId === a.aspirationId);
        return (
          <Pressable key={a.aspirationId} accessibilityRole="button" style={[styles.chip, { backgroundColor: `${color}1F` }]} onPress={() => onOpen?.(a)}>
            <View style={[styles.dot, { backgroundColor: color }]} />
            <Text variant="label" numberOfLines={1}>
              {mine.length > 0 ? `${a.shortName} · ${mine.filter((x) => x.checkIn).length}/${mine.length}` : a.shortName}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** Something waiting on the user, as a slim two-line pill. */
export function WaitingPill({ icon, title, sub, onPress }: { icon: keyof typeof Ionicons.glyphMap; title: string; sub: string; onPress: () => void }) {
  const { theme } = useUnistyles();
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={({ pressed }) => [styles.pill, pressed && styles.pressed]}>
      <Ionicons name={icon} size={16} color={theme.colors.accent} />
      <View style={styles.flex}>
        <Text variant="label" style={styles.bold} numberOfLines={1}>{title}</Text>
        <Text variant="caption" muted numberOfLines={1}>{sub}</Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  now: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm, paddingVertical: theme.space.md },
  quicks: { flexDirection: 'row', gap: 6 },
  quick: { width: 36, height: 36, borderRadius: 18, alignItems: 'center', justifyContent: 'center' },
  pressed: { opacity: 0.7 },
  flex: { flex: 1 },
  flexGap: { flex: 1, gap: 2 },
  shrink: { flexShrink: 1 },
  bold: { fontWeight: '700' },
  semibold: { fontWeight: '600' },
  struck: { textDecorationLine: 'line-through' },
  accent: { color: theme.colors.accent },
  warn: { color: theme.colors.warn },
  rowItem: { flexDirection: 'row', gap: 10, alignItems: 'center' },
  timeCol: { width: 56 },
  timeSub: { flexDirection: 'row', alignItems: 'center', gap: 3 },
  dim: { opacity: 0.6 },
  actionCard: { paddingVertical: 10 },
  cardRow: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  meta: { flexDirection: 'row', alignItems: 'center', gap: 6, flexWrap: 'wrap' },
  outlined: { borderColor: theme.colors.accent, borderWidth: 1.5 },
  nowLine: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  nowText: { width: 56 },
  nowDot: { width: 8, height: 8, borderRadius: 4, backgroundColor: theme.colors.accent },
  rule: { flex: 1, height: 1.5, backgroundColor: `${theme.colors.accent}66` },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 10, paddingVertical: 6, borderRadius: theme.radii.pill },
  dot: { width: 8, height: 8, borderRadius: 4 },
  pill: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.sm,
    paddingHorizontal: 10,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.accentTint,
  },
}));
