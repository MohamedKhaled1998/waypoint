import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { settleOffer, stepFractions } from '../../progress/progress';
import { ProjectionText, StatePill, StepBar, WeekCardView } from '../../progress/ProgressParts';
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

export default function ProgressScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { value, failed } = useProgress();
  // A paused Aspiration shows as a dashed row with Resume.
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const pauseOf = new Map(progressRows(plans, paused.state).map((r) => [r.aspirationId, r.paused]));
  const full = plans.length >= MAX_ASPIRATIONS;

  if (!value) return <EmptyState title={failed ? t('progress.loadFailed') : t('common.loading')} body="" />;
  if (value.aspirations.length === 0)
    return (
      <View style={styles.empty}>
        <EmptyState title={t('progress.emptyTitle')} body={t('progress.emptyBody')} />
        <Card style={styles.interviewCard}>
          <Text variant="heading">{t('interview.startTitle')}</Text>
          <Text muted>{t('interview.startBody')}</Text>
          <Button title={t('today.interviewCta')} onPress={() => router.push('/interview')} />
        </Card>
      </View>
    );

  return (
    <ScrollView contentContainerStyle={styles.content} style={styles.screen}>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ disabled: full }}
        disabled={full}
        onPress={() => router.push('/interview')}
        style={[styles.add, full && styles.addFull]}
      >
        <Text variant="label">{t('progress.addAspiration')}</Text>
        {plans.length > 0 ? <Text variant="caption" muted>{t('progress.aspirationCount', { current: plans.length, max: MAX_ASPIRATIONS })}</Text> : null}
      </Pressable>
      <Card>
        <View style={styles.weeks}>
          <WeekCardView title={t('progress.thisWeek')} week={value.thisWeek} />
          <WeekCardView title={t('progress.lastWeek')} week={value.lastWeek} />
        </View>
      </Card>
      {unfinished ? <Button kind="secondary" title={t('progress.continueInterview')} onPress={() => router.push('/interview')} /> : null}
      {value.aspirations.map((a) => (
        <Pressable
          key={a.aspirationId}
          accessibilityRole="button"
          onPress={() => router.push({ pathname: '/aspiration/[id]', params: { id: a.aspirationId } })}
        >
          <Card hue={pauseOf.get(a.aspirationId) ? undefined : a.hue} style={pauseOf.get(a.aspirationId) ? styles.pausedCard : styles.aspiration}>
            <Text variant="heading">{a.shortName}</Text>
            {pauseOf.get(a.aspirationId) ? (
              <View style={styles.paused}>
                <Text muted>
                  {pauseOf.get(a.aspirationId)!.until ? t('pause.until', { date: pauseOf.get(a.aspirationId)!.until }) : t('pause.open')}
                </Text>
                <Button kind="secondary" title={t('pause.resume')} onPress={() => void paused.resume(pauseOf.get(a.aspirationId)!.pauseId)} />
              </View>
            ) : null}
            {a.hasPlan ? (
              <>
                <StepBar steps={stepFractions(a.milestonesReached, a.milestonesTotal)} hue={a.hue} />
                <Text muted variant="caption">
                  {t('progress.milestonesOf', { reached: a.milestonesReached, total: a.milestonesTotal })}
                  {a.pace !== null ? ` · ${t('progress.pace', { percent: Math.round(a.pace * 100) })}` : ''}
                </Text>
                {a.next ? (
                  <>
                    <Text variant="label">{t('progress.next', { title: a.next.title })}</Text>
                    <ProjectionText projection={a.next.projection} />
                    <StatePill state={a.state} />
                    {settleOffer(a)?.mutedLine ? (
                      <Text muted variant="caption">
                        {t('progress.settleChoice.mutedLine')}
                      </Text>
                    ) : null}
                  </>
                ) : (
                  <Text muted>{t('progress.noMilestone')}</Text>
                )}
              </>
            ) : (
              <Text muted>{t('progress.drafting')}</Text>
            )}
          </Card>
        </Pressable>
      ))}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  screen: { backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, gap: theme.space.md },
  weeks: { gap: theme.space.lg },
  aspiration: { gap: theme.space.sm },
  empty: { flex: 1 },
  interviewCard: { margin: theme.space.lg, gap: theme.space.sm },
  pausedCard: { gap: theme.space.sm, borderStyle: 'dashed', backgroundColor: 'transparent' },
  paused: { gap: theme.space.sm, marginTop: theme.space.sm },
  add: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    padding: theme.space.md, borderRadius: theme.radii.md, borderWidth: 1, borderColor: theme.colors.border, backgroundColor: theme.colors.surface,
  },
  addFull: { opacity: 0.6 },
}));
