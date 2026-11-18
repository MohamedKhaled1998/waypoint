import { Pressable, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useUnfinishedInterview } from '../../interview/useInterview';
import { usePlanBanners } from '../../plan/usePlan';
import { Button } from '../../ui/Button';
import { Banner } from '../../ui/Banner';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

export default function TodayScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const unfinished = useUnfinishedInterview();
  const { drafting, late, retryDraft, waitingVersionId } = usePlanBanners();
  return (
    <View style={styles.container}>
      {drafting ? <Banner message={t('plan.drafting')} /> : null}
      {late ? (
        <View style={styles.late}>
          <Banner message={t('plan.late')} />
          <Button kind="secondary" title={t('plan.retry')} onPress={() => void retryDraft()} />
        </View>
      ) : null}
      {waitingVersionId ? (
        <Pressable accessibilityRole="button" onPress={() => router.push({ pathname: '/plan-review', params: { id: waitingVersionId } })}>
          <Banner message={t('plan.waiting')} />
        </Pressable>
      ) : null}
      <Card>
        <Text variant="heading">{t(unfinished ? 'interview.continueTitle' : 'interview.startTitle')}</Text>
        <Text muted>{t(unfinished ? 'interview.continueBody' : 'interview.startBody')}</Text>
        <Button title={t('today.interviewCta')} onPress={() => router.push('/interview')} />
      </Card>
      <EmptyState title={t('today.emptyTitle')} body={t('today.emptyBody')} />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, padding: theme.space.lg, gap: theme.space.lg, backgroundColor: theme.colors.background },
  late: { gap: theme.space.sm },
}));
