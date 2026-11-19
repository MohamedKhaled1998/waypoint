import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import type { Hue } from '../theme/tokens';
import { Text } from '../ui/Text';
import { hoursLabel, projectionLine, type MilestoneRow, type Projection, type ProgressState, type WeekCard } from './progress';

/** A state pill: Too early, On track, Slipping, Ahead or Your call. */
export function StatePill({ state }: { state: ProgressState }) {
  const { t } = useTranslation();
  return (
    <View style={[styles.pill, state === 'slipping' || state === 'past_settle_for' ? styles.warn : null]}>
      <Text variant="caption">{t(`progress.states.${state}`)}</Text>
    </View>
  );
}

export function ProjectionText({ projection }: { projection: Projection | null }) {
  const { t } = useTranslation();
  if (!projection) return null;
  return (
    <Text muted variant="caption">
      {projectionLine(projection, (key, values) => t(key, values ?? {}))}
    </Text>
  );
}

/** A bar of filled and empty steps, one per Milestone. */
export function StepBar({ steps, hue }: { steps: boolean[]; hue: Hue }) {
  return (
    <View style={styles.steps}>
      {steps.map((reached, i) => (
        <View key={i} style={[styles.step, reached ? styles.hueFill(hue) : null]} />
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

/** This week / Last week: hours given against planned in total, a bar per Aspiration by short name and hue, and one line. */
export function WeekCardView({ title, week }: { title: string; week: WeekCard }) {
  const { t } = useTranslation();
  return (
    <View style={styles.week}>
      <View style={styles.row}>
        <Text variant="label">{title}</Text>
        <Text variant="label">{t('progress.hoursOf', { done: hoursLabel(week.doneMinutes), planned: hoursLabel(week.plannedMinutes) })}</Text>
      </View>
      {week.aspirations.map((a) => (
        <View key={a.aspirationId} style={styles.aspirationBar}>
          <Text variant="caption" style={styles.shortName}>
            {a.shortName}
          </Text>
          <Bar done={a.doneMinutes} planned={a.plannedMinutes} hue={a.hue} />
        </View>
      ))}
      <Text muted variant="caption">
        {t(`progress.weekLine.${week.line}`)}
      </Text>
    </View>
  );
}

/** One Milestone on the Route: tick, "You are here", Projection, state pill, planned date and "reached when" line. */
export function RouteStep({ milestone, hue, date }: { milestone: MilestoneRow; hue: Hue; date: string }) {
  const { t } = useTranslation();
  const reached = milestone.state === 'reached';
  return (
    <View style={styles.routeStep}>
      <View style={[styles.dot, reached ? styles.hueFill(hue) : null]}>{reached ? <Text variant="caption">✓</Text> : null}</View>
      <View style={styles.routeBody}>
        {milestone.youAreHere ? <Text variant="caption">{t('progress.youAreHere')}</Text> : null}
        <Text variant="label">{milestone.title}</Text>
        <Text muted variant="caption">
          {t('progress.plannedFor', { date })}
        </Text>
        <Text muted variant="caption">
          {t('progress.reachedWhen', { line: milestone.reachedWhen })}
        </Text>
        <ProjectionText projection={milestone.projection} />
        {milestone.pill ? <StatePill state={milestone.pill} /> : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
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
  step: { flex: 1, height: 6, borderRadius: theme.radii.pill, backgroundColor: theme.colors.border },
  hueFill: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  week: { gap: theme.space.sm },
  row: { flexDirection: 'row', justifyContent: 'space-between' },
  aspirationBar: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  shortName: { width: 96 },
  track: { flex: 1, height: 8, borderRadius: theme.radii.pill, backgroundColor: theme.colors.border, overflow: 'hidden' },
  fill: { height: 8, borderRadius: theme.radii.pill },
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
  routeBody: { flex: 1, gap: theme.space.xs, paddingBottom: theme.space.lg },
}));
