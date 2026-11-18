import { useState } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { usePlanBanners, usePlanSummaries } from '../../plan/usePlan';
import { ActionSheet } from '../../today/ActionSheet';
import { AspirationChips, NowCard, NowLine, TimelineRow, WaitingPill } from '../../today/TodayParts';
import { buildToday, type CheckInState, type ScheduledActionView } from '../../today/today';
import { actionById, localNow, useToday } from '../../today/useToday';
import { Banner } from '../../ui/Banner';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

export default function TodayScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { drafting, late, retryDraft, waitingVersionId } = usePlanBanners();
  const { plans } = usePlanSummaries();
  const { actions, loaded, task, undo, failed } = useToday();
  const [sheet, setSheet] = useState<{ action: ScheduledActionView; choice?: CheckInState } | null>(null);

  const now = localNow();
  const today = buildToday(actions, now);
  const hour = Number(now.time.slice(0, 2));
  const greeting = t(hour < 12 ? 'today.greeting.morning' : hour < 18 ? 'today.greeting.afternoon' : 'today.greeting.evening');
  const nowId = today.nowCard?.action.id;
  const undoAction = actionById(actions, undo?.scheduledActionId);

  const pills = (
    <View style={styles.pills}>
      {unfinished ? <WaitingPill label={t('today.pillInterview')} onPress={() => router.push('/interview')} /> : null}
      {waitingVersionId ? (
        <WaitingPill label={t('today.pillPlan')} onPress={() => router.push({ pathname: '/plan-review', params: { id: waitingVersionId } })} />
      ) : null}
    </View>
  );

  return (
    <View style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        {drafting ? <Banner message={t('plan.drafting')} /> : null}
        {late ? (
          <View style={styles.late}>
            <Banner message={t('plan.late')} />
            <Button kind="secondary" title={t('plan.retry')} onPress={() => void retryDraft()} />
          </View>
        ) : null}
        {unfinished || waitingVersionId ? pills : null}

        {today.state === 'none' ? (
          <>
            {!unfinished && plans.length === 0 ? (
              <Card>
                <Text variant="heading">{t('interview.startTitle')}</Text>
                <Text muted>{t('interview.startBody')}</Text>
                <Button title={t('today.interviewCta')} onPress={() => router.push('/interview')} />
              </Card>
            ) : null}
            {loaded ? <EmptyState title={t('today.emptyTitle')} body={t('today.emptyBody')} /> : null}
          </>
        ) : (
          <>
            <View style={styles.header}>
              <Text variant="title">{greeting}</Text>
              {today.state !== 'rest' ? (
                <Text muted>
                  {t('today.summary', {
                    done: today.summary.checkedIn,
                    total: today.summary.total,
                    minutesDone: today.summary.minutesDone,
                    minutesPlanned: today.summary.minutesPlanned,
                  })}
                </Text>
              ) : null}
              <AspirationChips aspirations={plans} />
            </View>

            {today.nowCard ? (
              <NowCard
                now={today.nowCard}
                onOpen={(action) => setSheet({ action })}
                onQuick={(action, state) => void task.quick(action, state)}
                onPartly={(action) => setSheet({ action, choice: 'partly' })}
              />
            ) : null}

            {today.state === 'allCheckedIn' ? (
              <Card>
                <Text variant="heading">{t('today.allCheckedInTitle')}</Text>
                {today.tomorrowFirst ? (
                  <Text muted>{t('today.tomorrowFirst', { title: today.tomorrowFirst.title, time: today.tomorrowFirst.slotStart })}</Text>
                ) : null}
              </Card>
            ) : null}

            {today.state === 'rest' ? (
              <Card>
                <Text variant="heading">{t('today.restTitle')}</Text>
                <Text muted>{t('today.restBody')}</Text>
              </Card>
            ) : null}

            <View style={styles.timeline}>
              {today.timeline.map((action, i) => (
                <View key={action.id} style={styles.timeline}>
                  {i === today.nowLineBefore ? <NowLine /> : null}
                  <TimelineRow action={action} aspirations={plans} isNow={action.id === nowId} onOpen={(a) => setSheet({ action: a })} />
                </View>
              ))}
              {today.nowLineBefore >= today.timeline.length && today.timeline.length > 0 ? <NowLine /> : null}
            </View>
          </>
        )}
      </ScrollView>

      {failed ? (
        <View style={styles.undo}>
          <Text style={styles.undoText}>{t('today.sheet.failed')}</Text>
        </View>
      ) : null}

      {undo && undoAction ? (
        <View style={styles.undo}>
          <Text style={styles.undoText}>{t('today.undone', { state: t(`today.state.${undo.state}`), title: undoAction.title })}</Text>
          <Pressable accessibilityRole="button" onPress={() => void task.undo()}>
            <Text variant="label" style={styles.undoText}>
              {t('today.undo')}
            </Text>
          </Pressable>
        </View>
      ) : null}

      <ActionSheet
        action={sheet?.action ?? null}
        initialChoice={sheet?.choice}
        onClose={() => setSheet(null)}
        onSubmit={(req) => task.submit(req)}
      />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, gap: theme.space.lg, flexGrow: 1 },
  late: { gap: theme.space.sm },
  header: { gap: theme.space.sm },
  pills: { flexDirection: 'row', gap: theme.space.sm },
  timeline: { gap: theme.space.sm },
  undo: {
    position: 'absolute',
    start: theme.space.lg,
    end: theme.space.lg,
    bottom: theme.space.lg,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: theme.space.lg,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.text,
  },
  undoText: { color: theme.colors.background },
}));
