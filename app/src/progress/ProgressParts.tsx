import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Pill } from '../ui/Pill';
import type { Hue } from '../theme/tokens';
import { Text } from '../ui/Text';
import { Pressable } from 'react-native';
import { hoursLabel, projectionLine, type MilestoneRow, type Projection, type ProgressState, type SettleChoice, type SettleOption, type WeekCard } from './progress';
import { useState } from 'react';
import { Card } from '../ui/Card';
import { ListRow } from '../ui/ListRow';
import Ionicons from '@expo/vector-icons/Ionicons';
import { lowerFirst, upperFirst } from '../i18n/text';

/** The colour of a Projection state: muted, done, warn or accent. */
export function useStateColor() {
  const { theme } = useUnistyles();
  return (state: ProgressState) =>
    ({ too_early: theme.colors.textMuted, on_track: theme.colors.done, slipping: theme.colors.warn, ahead: theme.colors.accent, past_settle_for: theme.colors.warn })[state] ??
    theme.colors.textMuted;
}

/** A state pill outlined in its colour: Too early, On track, Slipping, Ahead or Your call. */
export function StatePill({ state }: { state: ProgressState }) {
  const { t } = useTranslation();
  const color = useStateColor();
  return <Pill label={t(`progress.states.${state}`)} color={color(state)} />;
}

/** "Pace 85%", coloured by how close it is to the Plan. */
export function PaceText({ pace }: { pace: number }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const color = pace >= 0.8 ? theme.colors.done : pace >= 0.6 ? theme.colors.partly : theme.colors.warn;
  return <Text variant="label" style={{ color }}>{t('progress.pace', { percent: Math.round(pace * 100) })}</Text>;
}

/** Weeks kept up, as a row of dots (filled for the weeks kept). */
export function WeekDots({ kept, of, hue }: { kept: number; of: number; hue?: Hue }) {
  const { theme } = useUnistyles();
  const color = hue ? theme.colors.hues[hue].solid : theme.colors.done;
  return (
    <View style={styles.weekDots}>
      {Array.from({ length: of }, (_, i) => (
        <View key={i} style={[styles.weekDot, i < kept ? { backgroundColor: color, borderColor: color } : null]} />
      ))}
    </View>
  );
}

export function ProjectionText({ projection }: { projection: Projection | null }) {
  const { t } = useTranslation();
  if (!projection) return null;
  return (
    <Text muted variant="caption">
      {upperFirst(projectionLine(projection, (key, values) => t(key, values ?? {})))}
    </Text>
  );
}

/** A bar of steps, one per Milestone: reached in the hue, the current one half-tinted, the rest empty. */
export function StepBar({ steps, hue }: { steps: boolean[]; hue: Hue }) {
  const { theme } = useUnistyles();
  const solid = theme.colors.hues[hue].solid;
  const current = steps.indexOf(false);
  return (
    <View style={styles.steps}>
      {steps.map((reached, i) => (
        <View key={i} style={[styles.step, reached ? { backgroundColor: solid } : i === current ? { backgroundColor: `${solid}66` } : null]} />
      ))}
    </View>
  );
}

function Bar({ done, planned, hue }: { done: number; planned: number; hue: Hue }) {
  return (
    <View style={styles.track}>
      <View style={[styles.fill, styles.hueFill(hue), { width: `${planned > 0 ? Math.min(100, (done / planned) * 100) : 0}%` }]} />
    </View>
  );
}

/** This week / Last week as a toggle: hours given against planned, a bar per Aspiration, and one line. */
export function WeekCardView({ thisWeek, lastWeek }: { thisWeek: WeekCard; lastWeek: WeekCard }) {
  const { t } = useTranslation();
  const [last, setLast] = useState(false);
  const week = last ? lastWeek : thisWeek;
  return (
    <Card style={styles.week}>
      <View style={styles.toggle}>
        {([false, true] as const).map((isLast) => (
          <Pressable key={String(isLast)} accessibilityRole="tab" accessibilityState={{ selected: last === isLast }} onPress={() => setLast(isLast)}
            style={[styles.toggleItem, last === isLast && styles.toggleOn]}>
            <Text variant="label" style={last === isLast ? styles.accentText : styles.mutedText}>{t(isLast ? 'progress.lastWeek' : 'progress.thisWeek')}</Text>
          </Pressable>
        ))}
      </View>
      <Text variant="heading">{t('progress.hoursOfLong', { done: hoursLabel(week.doneMinutes), planned: hoursLabel(week.plannedMinutes) })}</Text>
      <View style={styles.bars}>
        {week.aspirations.map((a) => (
          <View key={a.aspirationId} style={styles.barRow}>
            <View style={styles.row}>
              <Text variant="label" style={styles.regular}>{a.shortName}</Text>
              <Text variant="label" muted style={styles.regular}>{t('progress.minutesOf', { done: a.doneMinutes, planned: a.plannedMinutes })}</Text>
            </View>
            <Bar done={a.doneMinutes} planned={a.plannedMinutes} hue={a.hue} />
          </View>
        ))}
      </View>
      <Text variant="label" muted style={styles.regular}>{t(`progress.weekLine.${week.line}`)}</Text>
    </Card>
  );
}

/** The calm Settle-for card: one line, then three options as rows, each opening the Coach with that choice in context. */
export function SettleCard({ choice, onPick }: { choice: SettleChoice; onPick: (option: SettleOption) => void }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [open, setOpen] = useState(false);
  return (
    <Card style={styles.settle}>
      <Pressable accessibilityRole="button" accessibilityState={{ expanded: open }} onPress={() => setOpen(!open)} style={styles.settleHead}>
        <Ionicons name="calendar-outline" size={18} color={theme.colors.accent} />
        <View style={styles.flexGap}>
          <Text variant="label">{t('progress.settleChoice.title')}</Text>
          <Text variant="label" muted style={styles.regular}>{t('progress.settleChoice.body')}</Text>
        </View>
        <Ionicons name={open ? 'chevron-up' : 'chevron-down'} size={16} color={theme.colors.textMuted} />
      </Pressable>
      {open ? (
        <>
          {choice.options.map((option, i) => (
            <ListRow
              key={option}
              title={t(`progress.settleChoice.options.${option}`)}
              subtitle={t(`progress.settleChoice.sub.${option}`)}
              onPress={() => onPick(option)}
              last={i === choice.options.length - 1}
            />
          ))}
          <Text variant="caption" muted>{t('progress.settleChoice.footer')}</Text>
        </>
      ) : null}
    </Card>
  );
}

/**
 * One Milestone on the Route: a node on a connecting line (a tick once reached), its title with the state pill at
 * the end (the next one only), the planned date with its "reached when" line, and the Projection for the next one.
 */
export function RouteStep({ milestone, hue, date, last }: { milestone: MilestoneRow; hue: Hue; date: string; last: boolean }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const reached = milestone.state === 'reached';
  const solid = theme.colors.hues[hue].solid;
  return (
    <View style={styles.routeStep}>
      <View style={styles.rail}>
        <View style={[styles.node, { borderColor: solid }, reached && { backgroundColor: solid }]}>
          {reached ? <Ionicons name="checkmark" size={10} color={theme.colors.onAccent} /> : null}
        </View>
        {!last ? <View style={styles.connector} /> : null}
      </View>
      <View style={styles.routeBody}>
        <View style={styles.titleRow}>
          <Text style={[styles.flexText, styles.semibold]}>{milestone.title}</Text>
          {milestone.youAreHere && milestone.pill ? <StatePill state={milestone.pill} /> : null}
        </View>
        <Text variant="label" muted style={styles.regular}>
          {t('progress.plannedReached', { date, line: lowerFirst(milestone.reachedWhen) })}
        </Text>
        {/* Only the next Milestone says where it stands; the rest would repeat it. */}
        {milestone.youAreHere ? <ProjectionText projection={milestone.projection} /> : null}
      </View>
    </View>
  );
}

/** "You are here · 18 Nov": where today sits on the Route, before the next Milestone. */
export function YouAreHere({ date, hue }: { date: string; hue: Hue }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  return (
    <View style={styles.routeStep}>
      <View style={styles.rail}>
        <View style={[styles.here, { backgroundColor: theme.colors.hues[hue].solid }]} />
        <View style={styles.connector} />
      </View>
      <Text variant="caption" style={[styles.routeBody, { color: theme.colors.hues[hue].solid }]}>{t('progress.youAreHereOn', { date })}</Text>
    </View>
  );
}

/** The Route's end: the Hoped-for date, flagged. */
export function RouteEnd({ label }: { label: string }) {
  const { theme } = useUnistyles();
  return (
    <View style={styles.routeStep}>
      <View style={styles.rail}>
        <Ionicons name="flag" size={16} color={theme.colors.accent} />
      </View>
      <Text variant="label" style={styles.accentText}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  flexGap: { flex: 1, gap: 2 },
  flexText: { flex: 1 },
  semibold: { fontWeight: '600' },
  rail: { alignItems: 'center', width: 18 },
  node: { width: 16, height: 16, borderRadius: 8, borderWidth: 2, marginTop: 2, alignItems: 'center', justifyContent: 'center', backgroundColor: theme.colors.background },
  here: { width: 10, height: 10, borderRadius: 5, marginTop: 4 },
  connector: { width: 2, flex: 1, minHeight: 12, backgroundColor: theme.colors.border },
  titleRow: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  regular: { fontWeight: '400' },
  accentText: { color: theme.colors.accent, fontWeight: '700' },
  mutedText: { color: theme.colors.textMuted, fontWeight: '700' },
  toggle: { flexDirection: 'row', gap: 6 },
  toggleItem: { paddingHorizontal: 10, paddingVertical: theme.space.xs, borderRadius: theme.radii.pill },
  toggleOn: { backgroundColor: theme.colors.accentTint },
  bars: { gap: theme.space.sm },
  barRow: { gap: 3 },
  weekDots: { flexDirection: 'row', gap: theme.space.xs },
  weekDot: { width: 12, height: 12, borderRadius: 6, borderWidth: 1.5, borderColor: theme.colors.border },
  pill: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.xs,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.accentTint,
  },
  warn: { backgroundColor: theme.colors.warningTint },
  steps: { flexDirection: 'row', gap: theme.space.xs },
  step: { flex: 1, height: 6, borderRadius: 3, backgroundColor: theme.colors.surfaceAlt },
  hueFill: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  week: { gap: theme.space.sm },
  row: { flexDirection: 'row', justifyContent: 'space-between' },
  aspirationBar: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  shortName: { width: 96 },
  track: { height: 6, borderRadius: 3, backgroundColor: theme.colors.surfaceAlt, overflow: 'hidden' },
  fill: { height: 6, borderRadius: 3 },
  settle: { gap: theme.space.sm },
  settleHead: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  settleOption: { padding: theme.space.md, borderRadius: theme.radii.md, borderWidth: 1, borderColor: theme.colors.border },
  routeStep: { flexDirection: 'row', gap: theme.space.md },
  dot: {
    width: 20,
    height: 20,
    borderRadius: theme.radii.pill,
    borderWidth: 2,
    borderColor: theme.colors.border,
    alignItems: 'center',
    justifyContent: 'center',
  },
  routeBody: { flex: 1, gap: 2, paddingBottom: theme.space.lg },
}));
