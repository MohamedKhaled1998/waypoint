import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { formatDay } from '../../i18n/dates';
import { Chevron } from '../../ui/Chevron';
import { Section } from '../../ui/Section';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { consistencyLine } from '../../lifecycle/lifecycle';
import { settleOffer, stepFractions } from '../../progress/progress';
import { PaceText, ProjectionText, StatePill, StepBar, WeekCardView, WeekDots } from '../../progress/ProgressParts';
import { useProgress } from '../../progress/useProgress';
import { usePlanSummaries } from '../../plan/usePlan';
import { progressRows } from '../../pause/pause';
import { usePauses } from '../../pause/usePauses';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

/** At most this many Aspirations are active at once; they share one Time budget. */
const MAX_ASPIRATIONS = 3;

/** Paused Aspirations first, then the active ones. */
function splitPaused<T extends { aspirationId: string }>(all: T[], pauseOf: Map<string, unknown>): [T[], T[]] {
  const paused: T[] = [];
  const active: T[] = [];
  for (const a of all) (pauseOf.get(a.aspirationId) ? paused : active).push(a);
  return [paused, active];
}

export default function ProgressScreen() {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { value, failed } = useProgress();
  // A paused Aspiration shows as a dashed row with Resume.
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const pauseOf = new Map(progressRows(plans, paused.state).map((r) => [r.aspirationId, r.paused]));
  const full = plans.length >= MAX_ASPIRATIONS;

  const open = (id: string) => router.push({ pathname: '/aspiration/[id]', params: { id } });
  const interviewPill = unfinished ? (
    <Pressable accessibilityRole="button" onPress={() => router.push('/interview')} style={({ pressed }) => [styles.interviewPill, pressed && styles.pressed]}>
      <Ionicons name="chatbubble-ellipses-outline" size={16} color={theme.colors.accent} />
      <Text variant="label" style={[styles.flex, styles.accentBold]}>{t('progress.interviewPill')}</Text>
      <Chevron />
    </Pressable>
  ) : null;

  if (!value) return <EmptyState title={failed ? t('progress.loadFailed') : t('common.loading')} body="" />;
  if (value.aspirations.length === 0 && (value.maintenance ?? []).length === 0 && (value.closed ?? []).length === 0)
    return (
      <ScrollView contentContainerStyle={styles.content} style={styles.screen}>
        <View style={styles.emptyHead}>
          <Ionicons name="trail-sign-outline" size={44} color={theme.colors.accent} />
          <Text variant="title" style={styles.centered}>{t('progress.emptyTitle')}</Text>
          <Text muted style={styles.centered}>{t('progress.emptyBody')}</Text>
        </View>
        <Card tone="accent" onPress={() => router.push('/interview')}>
          <View style={styles.headRow}>
            <Ionicons name="chatbubble-ellipses-outline" size={18} color={theme.colors.accent} />
            <Text variant="heading" style={styles.flex}>{unfinished ? t('interview.continueTitle') : t('interview.startTitle')}</Text>
            <Chevron />
          </View>
          <Text variant="label" muted style={styles.regular}>{unfinished ? t('interview.continueBody') : t('interview.startBody')}</Text>
        </Card>
      </ScrollView>
    );

  const [pausedOnes, active] = splitPaused(value.aspirations, pauseOf);

  return (
    <ScrollView contentContainerStyle={styles.content} style={styles.screen}>
      <View style={styles.between}>
        <Text variant="label" muted style={styles.regular}>{t('progress.aspirationCount', { current: plans.length, max: MAX_ASPIRATIONS })}</Text>
        <Button
          kind="secondary"
          icon="add"
          title={full ? t('progress.addFull', { max: MAX_ASPIRATIONS }) : t('progress.addAspiration')}
          onPress={() => (full ? router.navigate('/coach') : router.push('/interview'))}
        />
      </View>
      <WeekCardView thisWeek={value.thisWeek} lastWeek={value.lastWeek} />
      {interviewPill}

      {active.length > 0 ? (
        <Section title={t('progress.activeSection')}>
          <View style={styles.cards}>
            {active.map((a) => (
              <Card key={a.aspirationId} hue={a.hue} onPress={() => open(a.aspirationId)}>
                <View style={styles.headRow}>
                  <Text variant="heading" style={styles.flex}>{a.ownWords || a.shortName}</Text>
                  <Chevron />
                </View>
                {a.hasPlan ? (
                  <>
                    <View style={styles.stepBar}>
                      <StepBar steps={stepFractions(a.milestonesReached, a.milestonesTotal)} hue={a.hue} />
                    </View>
                    <View style={styles.between}>
                      <Text variant="label" muted style={styles.regular}>{t('progress.milestonesOf', { reached: a.milestonesReached, total: a.milestonesTotal })}</Text>
                      {a.pace !== null ? <PaceText pace={a.pace} /> : null}
                    </View>
                    {a.next ? (
                      <View style={styles.nextRow}>
                        <View style={styles.flex}>
                          <Text variant="label">{a.next.title}</Text>
                          <ProjectionText projection={a.next.projection} />
                        </View>
                        <StatePill state={a.state} />
                      </View>
                    ) : (
                      <Text variant="label" muted style={styles.regular}>{t('progress.noMilestone')}</Text>
                    )}
                    {settleOffer(a)?.mutedLine ? (
                      <View style={styles.headRow}>
                        <Ionicons name="calendar-outline" size={15} color={theme.colors.accent} />
                        <Text variant="label" muted style={[styles.flex, styles.regular]}>{t('progress.settleChoice.mutedLine')}</Text>
                      </View>
                    ) : null}
                  </>
                ) : (
                  <Text variant="label" muted style={styles.regular}>{t('progress.drafting')}</Text>
                )}
              </Card>
            ))}
          </View>
        </Section>
      ) : null}

      {(value.maintenance ?? []).length > 0 ? (
        <Section title={t('progress.maintenanceSection')}>
          <View style={styles.cards}>
            {value.maintenance.map((m) => (
              <Card key={m.aspirationId} onPress={() => open(m.aspirationId)}>
                <View style={styles.headRow}>
                  <Ionicons name="leaf-outline" size={18} color={theme.colors.hues[m.hue].solid} />
                  <Text style={[styles.flex, styles.bold]}>{m.shortName}</Text>
                  <Chevron />
                </View>
                <View style={styles.headRow}>
                  <WeekDots kept={m.consistency.weeksKeptUp} of={m.consistency.ofWeeks} hue={m.hue} />
                  <Text variant="label">{consistencyLine(m.consistency, t)}</Text>
                </View>
              </Card>
            ))}
          </View>
        </Section>
      ) : null}

      {pausedOnes.length > 0 ? (
        <Section title={t('progress.pausedSection')}>
          <View style={styles.cards}>
            {pausedOnes.map((a) => {
              const pause = pauseOf.get(a.aspirationId)!;
              return (
                <Pressable key={a.aspirationId} accessibilityRole="button" onPress={() => open(a.aspirationId)} style={styles.pausedRow}>
                  <Ionicons name="pause-circle" size={28} color={theme.colors.skipped} />
                  <View style={styles.flexGap}>
                    <Text muted style={styles.semibold}>{a.ownWords || a.shortName}</Text>
                    <Text variant="label" muted style={styles.regular}>
                      {pause.until ? t('progress.pausedUntil', { date: formatDay(pause.until, { locale: i18n.language }) }) : t('progress.pausedOpen')}
                    </Text>
                  </View>
                  <Button kind="secondary" title={t('pause.resume')} onPress={() => void paused.resume(pause.pauseId)} />
                </Pressable>
              );
            })}
          </View>
        </Section>
      ) : null}

      {(value.closed ?? []).length > 0 ? (
        <Section title={t('progress.historySection')}>
          <View style={styles.cards}>
            {(value.closed ?? []).map((c) => (
              <Card key={c.aspirationId} onPress={() => open(c.aspirationId)}>
                <View style={styles.headRow}>
                  <View style={[styles.dot, { backgroundColor: theme.colors.hues[c.hue].solid }]} />
                  <Text style={[styles.flex, styles.semibold]}>{c.shortName}</Text>
                  <Chevron />
                </View>
              </Card>
            ))}
          </View>
        </Section>
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  screen: { backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, paddingBottom: theme.space.xl, gap: 20 },
  between: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: theme.space.sm },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  cards: { gap: 10 },
  flex: { flex: 1 },
  flexGap: { flex: 1, gap: 2 },
  regular: { fontWeight: '400' },
  semibold: { fontWeight: '600' },
  bold: { fontWeight: '700' },
  centered: { textAlign: 'center' },
  accentBold: { color: theme.colors.accent, fontWeight: '700' },
  pressed: { opacity: 0.75 },
  stepBar: { marginTop: theme.space.xs },
  nextRow: { flexDirection: 'row', alignItems: 'flex-start', gap: theme.space.sm },
  dot: { width: 8, height: 8, borderRadius: 4 },
  emptyHead: { alignItems: 'center', gap: 10, paddingTop: 40 },
  interviewPill: { flexDirection: 'row', alignItems: 'center', gap: 6, padding: theme.space.md, borderRadius: theme.radii.md, backgroundColor: theme.colors.accentTint },
  pausedRow: {
    flexDirection: 'row', alignItems: 'center', gap: theme.space.md, padding: 14,
    borderRadius: theme.radii.lg, borderWidth: 1, borderStyle: 'dashed', borderColor: theme.colors.skipped,
  },
}));
