import { useEffect } from 'react';
import { View } from 'react-native';
import { Stack, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { firstRun } from '../firstrun/native';
import { usePlanBanners } from '../plan/usePlan';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

/** First-run step 4, after the Interview: the Plan is being drafted; it waits on Today if the user leaves. */
export default function DraftingScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { waitingVersionId } = usePlanBanners();

  // The moment the draft is ready, go straight to reviewing it.
  useEffect(() => {
    if (!waitingVersionId) return;
    void firstRun.advance('planReview').then(() => router.replace({ pathname: '/plan-review', params: { id: waitingVersionId } }));
  }, [waitingVersionId, router]);

  return (
    <View style={styles.container}>
      <Stack.Screen options={{ headerShown: false }} />
      <StepCounter step="drafting" />
      <View style={styles.body}>
        <Text variant="heading">{t('firstRun.draftingTitle')}</Text>
        <Text muted>{t('firstRun.draftingBody')}</Text>
      </View>
      <Button kind="secondary" title={t('firstRun.leaveForNow')} onPress={() => router.replace('/')} />
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: {
    flex: 1,
    gap: theme.space.lg,
    padding: theme.space.xl,
    paddingTop: theme.space.xl + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  body: { flex: 1, justifyContent: 'center', gap: theme.space.sm },
}));
