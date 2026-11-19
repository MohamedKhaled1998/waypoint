import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { stepFractions } from '../../progress/progress';
import { ProjectionText, StatePill, StepBar, WeekCardView } from '../../progress/ProgressParts';
import { useProgress } from '../../progress/useProgress';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

export default function ProgressScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { value, failed } = useProgress();

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
          <Card hue={a.hue} style={styles.aspiration}>
            <Text variant="heading">{a.shortName}</Text>
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
}));
