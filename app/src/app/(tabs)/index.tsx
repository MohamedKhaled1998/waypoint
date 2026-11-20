import Ionicons from '@expo/vector-icons/Ionicons';
import { useEffect, useState } from 'react';
import Animated, { FadeInDown, FadeOutDown } from 'react-native-reanimated';
import { Pressable, ScrollView, View } from 'react-native';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { firstRun, useFirstRun } from '../../firstrun/native';
import { tourCard } from '../../firstrun/firstRun';
import { formatDay } from '../../i18n/dates';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { remindersLookDead } from '../../pause/battery';
import { todayPausedLine } from '../../pause/pause';
import { usePauses } from '../../pause/usePauses';
import { openBatterySettings } from '../../push/native';
import { usePlanBanners, usePlanSummaries } from '../../plan/usePlan';
import { ActionSheet } from '../../today/ActionSheet';
import { AspirationChips, NowCard, NowLine, TimelineRow, WaitingPill } from '../../today/TodayParts';
import { buildToday, timeLabel, type CheckInState, type ScheduledActionView } from '../../today/today';
import { actionById, localNow, useToday } from '../../today/useToday';
import { Banner } from '../../ui/Banner';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Section } from '../../ui/Section';
import { Text } from '../../ui/Text';

type Summary = { pendingVersionId: string | null; currentVersionId: string | null; shortName: string };

function planWaiting<T extends Summary>(plans: T[], versionId: string | null | undefined) {
  return plans.find((p) => p.pendingVersionId === versionId);
}

function planInForce<T extends Summary>(plans: T[]) {
  return plans.find((p) => p.currentVersionId);
}

function countPartly(actions: ScheduledActionView[]) {
  return actions.filter((a) => a.checkIn?.state === 'partly').length;
}

export default function TodayScreen() {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { drafting, late, retryDraft, waitingVersionId } = usePlanBanners();
  const { plans } = usePlanSummaries();
  const { step: tourStep } = useFirstRun();
  const { actions, slots, loaded, task, undo, failed, move } = useToday();
  const paused = usePauses(plans);
  const pausedLine = todayPausedLine(paused.state, plans);
  const { battery } = useLocalSearchParams<{ battery?: string }>();
  const batteryHelp = battery === '1' || remindersLookDead(actions);
  const [sheet, setSheet] = useState<{ action: ScheduledActionView; choice?: CheckInState } | null>(null);

  // A reminder tap lands here with that Action's sheet open.
  const { action: tappedId } = useLocalSearchParams<{ action?: string }>();
  // Opened once per tap, then the param is cleared so a later cache change cannot reopen it.
  useEffect(() => {
    const tapped = tappedId ? actionById(actions, tappedId) : undefined;
    if (!tapped) return;
    setSheet({ action: tapped });
    router.setParams({ action: undefined });
  }, [tappedId, actions, router]);

  const now = localNow();
  const today = buildToday(actions, now);
  const hour = Number(now.time.slice(0, 2));
  const greeting = t(hour < 12 ? 'today.greeting.morning' : hour < 18 ? 'today.greeting.afternoon' : 'today.greeting.evening');
  const nowId = today.nowCard?.action.id;
  const tour = tourCard(tourStep, !!today.nowCard);
  const undoAction = actionById(actions, undo?.scheduledActionId);

  const pendingPlan = planWaiting(plans, waitingVersionId);
  const currentPlan = planInForce(plans);
  const pills =
    unfinished || waitingVersionId ? (
      <View style={styles.pills}>
        {waitingVersionId ? (
          <WaitingPill
            icon="sparkles-outline"
            title={t('today.pillPlan')}
            sub={t('today.pillPlanSub', { name: pendingPlan?.shortName ?? '' })}
            onPress={() => router.push({ pathname: '/plan-review', params: { id: waitingVersionId } })}
          />
        ) : null}
        {unfinished ? (
          <WaitingPill icon="chatbubble-ellipses-outline" title={t('today.pillInterview')} sub={t('today.pillInterviewSub')} onPress={() => router.push('/interview')} />
        ) : null}
      </View>
    ) : null;
  const partly = countPartly(today.timeline);
  const tomorrow = today.tomorrowFirst
    ? t('today.nextFirst', { title: today.tomorrowFirst.title, day: formatDay(today.tomorrowFirst.date, { locale: i18n.language }), time: timeLabel(today.tomorrowFirst) })
    : null;
  const summary =
    today.timeline.length > 0
      ? t('today.summaryDated', {
          date: formatDay(now.date, { locale: i18n.language }),
          done: today.summary.checkedIn,
          total: today.summary.total,
          minutesDone: today.summary.minutesDone,
          minutesPlanned: today.summary.minutesPlanned,
        })
      : formatDay(now.date, { locale: i18n.language });

  return (
    <View style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        {drafting ? <Banner message={t('plan.drafting')} /> : null}
        {late ? <Banner message={t('plan.late')} action={t('plan.retry')} onPress={() => void retryDraft()} /> : null}
        {tour === 'waiting' && today.tomorrowFirst ? (
          <Card tone="accent">
            <Text variant="caption" muted>{t('firstRun.step', { n: 1, of: 2 })}</Text>
            <Text variant="heading">{t('firstRun.tour.waitTitle')}</Text>
            <Text muted>
              {t('firstRun.tour.waitBody', {
                title: today.tomorrowFirst.title,
                day: formatDay(today.tomorrowFirst.date, { locale: i18n.language }),
                time: timeLabel(today.tomorrowFirst),
              })}
            </Text>
          </Card>
        ) : null}
        {tour === 'try' ? (
          <Card tone="accent">
            <Text variant="caption" muted>{t('firstRun.step', { n: 1, of: 2 })}</Text>
            <Text variant="heading">{t('firstRun.tour.tryTitle')}</Text>
            <Text muted>{t('firstRun.tour.tryBody')}</Text>
          </Card>
        ) : null}
        {tour === 'wrapUp' ? (
          <Card tone="accent">
            <Text variant="caption" muted>{t('firstRun.step', { n: 2, of: 2 })}</Text>
            <Text variant="heading">{t('firstRun.tour.wrapTitle')}</Text>
            <Text muted>{t('firstRun.tour.wrapBody')}</Text>
            <Button title={t('firstRun.tour.done')} onPress={() => void firstRun.finishTour()} style={styles.start} />
          </Card>
        ) : null}

        {batteryHelp ? <Banner message={t('battery.banner')} action={t('battery.open')} onPress={() => void openBatterySettings()} /> : null}

        {today.state === 'none' && !(pausedLine?.kind === 'everything') ? (
          <>
            {pills}
            {!unfinished && plans.length === 0 ? (
              <Card>
                <Text variant="heading">{t('interview.startTitle')}</Text>
                <Text muted>{t('interview.startBody')}</Text>
                <Button title={t('today.interviewCta')} onPress={() => router.push('/interview')} style={styles.start} />
              </Card>
            ) : null}
            {loaded ? <EmptyState title={t('today.emptyTitle')} body={t('today.emptyBody')} /> : null}
          </>
        ) : (
          <>
            <View style={styles.header}>
              <View style={styles.headLines}>
                <Text variant="title">{greeting}</Text>
                <Text variant="label" muted style={styles.regular}>{summary}</Text>
              </View>
              <AspirationChips aspirations={plans} actions={today.timeline} onOpen={(a) => router.push({ pathname: '/aspiration/[id]', params: { id: a.aspirationId } })} />
            </View>

            {today.nowCard ? (
              <NowCard
                now={today.nowCard}
                aspirations={plans}
                onOpen={(action) => setSheet({ action })}
                onQuick={(action, state) => {
                  void task.quick(action, state);
                  void firstRun.nowCardTapped(state);
                }}
                onPartly={(action) => setSheet({ action, choice: 'partly' })}
              />
            ) : null}

            {pills}

            {pausedLine?.kind === 'everything' ? (
              <Card>
                <Ionicons name="pause-circle-outline" size={28} color={theme.colors.accent} />
                <Text variant="title">
                  {t(pausedLine.byCoach ? 'pause.byCoach' : pausedLine.until ? 'pause.everythingUntil' : 'pause.everythingOpen', { date: pausedLine.until ? formatDay(pausedLine.until, { locale: i18n.language }) : '' })}
                </Text>
                <Text variant="label" muted style={styles.regular}>{t('today.pausedBody')}</Text>
                <View style={styles.buttons}>
                  <Button title={t('pause.resumeNow')} onPress={() => void paused.resume(pausedLine.pauseId)} />
                  <Button kind="quiet" title={t('today.changeDate')} onPress={() => router.push('/settings')} />
                </View>
              </Card>
            ) : null}

            {today.state === 'allCheckedIn' ? (
              <Card tone="accent">
                <Text style={styles.bold}>{t('today.allCheckedInTitle')}</Text>
                <Text variant="label" style={styles.regular}>
                  {[partly ? t('today.partlyCounts', { count: partly }) : null, tomorrow].filter(Boolean).join(' ')}
                </Text>
              </Card>
            ) : null}

            {today.state === 'rest' && pausedLine?.kind !== 'everything' ? (
              <Card style={styles.rest}>
                <Text variant="title">{t('today.restTitle')}</Text>
                <Text variant="label" muted style={styles.regular}>{[t('today.restBody'), tomorrow].filter(Boolean).join(' ')}</Text>
                <View style={styles.buttons}>
                  {currentPlan?.currentVersionId ? (
                    <Button kind="secondary" title={t('today.seeWeek')}
                      onPress={() => router.push({ pathname: '/plan-review', params: { id: currentPlan.currentVersionId!, tab: 'twoWeeks' } })} />
                  ) : null}
                  <Button kind="quiet" title={t('today.messageCoach')} onPress={() => router.navigate('/coach')} />
                </View>
              </Card>
            ) : null}

            {today.timeline.length > 0 ? (
              <Section title={t('today.timelineTitle')}>
                <View style={styles.timeline}>
                  {today.timeline.map((action, i) => (
                    <View key={action.id} style={styles.timeline}>
                      {i === today.nowLineBefore ? <NowLine time={now.time} /> : null}
                      <TimelineRow
                        action={action}
                        aspirations={plans}
                        isNow={action.id === nowId}
                        overdue={!action.checkIn && !!action.slotEnd && action.slotEnd < now.time}
                        onOpen={(a) => setSheet({ action: a })}
                        onQuick={(a, state) => void task.quick(a, state)}
                      />
                    </View>
                  ))}
                  {today.nowLineBefore >= today.timeline.length ? <NowLine time={now.time} /> : null}
                </View>
              </Section>
            ) : null}

            {pausedLine?.kind === 'aspirations' ? (
              <View style={styles.pausedNote}>
                <Ionicons name="pause-circle-outline" size={16} color={theme.colors.textMuted} />
                <Text variant="label" muted style={[styles.flex, styles.regular]}>{t('today.pausedNote', { names: pausedLine.names.join(', ') })}</Text>
              </View>
            ) : null}
            {today.plannedUntil && today.plannedUntil <= now.date ? <Text muted>{t('today.pastCache', { date: formatDay(today.plannedUntil, { locale: i18n.language }) })}</Text> : null}
          </>
        )}
      </ScrollView>

      {failed ? (
        <Animated.View entering={FadeInDown} exiting={FadeOutDown} style={styles.undo}>
          <Text variant="label" style={styles.undoText}>{t('today.sheet.failed')}</Text>
        </Animated.View>
      ) : null}

      {undo && undoAction ? (
        <Animated.View entering={FadeInDown} exiting={FadeOutDown} style={styles.undo}>
          <Text variant="label" style={[styles.undoText, styles.flex]} numberOfLines={1}>
            {t('today.undone', { state: t(`today.state.${undo.state}`), title: undoAction.title })}
          </Text>
          <Pressable accessibilityRole="button" hitSlop={8} onPress={() => void task.undo()}>
            <Text variant="label" style={styles.undoAction}>{t('today.undo')}</Text>
          </Pressable>
        </Animated.View>
      ) : null}

      <ActionSheet
        action={sheet?.action ?? null}
        initialChoice={sheet?.choice}
        onClose={() => setSheet(null)}
        onSubmit={(req) => task.submit(req)}
        slots={slots}
        history={actions}
        now={now}
        onMove={(date, slotId) => move(sheet!.action.id, date, slotId)}
        onPause={async (scope, until) => void (await paused.start(scope, until))}
        aspirations={plans}
      />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  // Room at the end, so the Undo toast never covers the last row.
  content: { padding: theme.space.lg, paddingBottom: 120, gap: theme.space.lg, flexGrow: 1 },
  header: { gap: 10 },
  headLines: { gap: 2 },
  regular: { fontWeight: '400' },
  bold: { fontWeight: '700' },
  flex: { flex: 1 },
  start: { alignSelf: 'flex-start' },
  pills: { flexDirection: 'row', gap: theme.space.sm },
  buttons: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  rest: { gap: 10, padding: 18 },
  timeline: { gap: theme.space.sm },
  pausedNote: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  undo: {
    position: 'absolute',
    start: theme.space.lg,
    end: theme.space.lg,
    bottom: theme.space.lg,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    padding: theme.space.md,
    paddingStart: theme.space.lg,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.text,
  },
  undoText: { color: theme.colors.background, fontWeight: '400' },
  undoAction: { color: theme.colors.accentTint, fontWeight: '700' },
}));
