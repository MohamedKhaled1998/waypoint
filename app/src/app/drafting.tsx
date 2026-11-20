import { useEffect } from 'react';
import Ionicons from '@expo/vector-icons/Ionicons';
import { ActivityIndicator, View } from 'react-native';
import { Stack, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Card } from '../ui/Card';
import { firstRun } from '../firstrun/native';
import { usePlanBanners } from '../plan/usePlan';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

/** First-run step 4, after the Interview: the Plan is being drafted; it waits on Today if the user leaves. */
export default function DraftingScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
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
      <ActivityIndicator color={theme.colors.accent} size="large" style={styles.start} />
      <Text variant="title">{t('firstRun.draftingTitle')}</Text>
      <Text muted>{t('firstRun.draftingBody')}</Text>
      <Card>
        <View style={styles.row}>
          <Ionicons name="chatbubble-ellipses-outline" size={18} color={theme.colors.accent} />
          <Text variant="label" style={[styles.flex, styles.regular]}>{t('firstRun.draftingSummary')}</Text>
        </View>
      </Card>
      <Text variant="label" muted style={styles.regular}>{t('firstRun.draftingClose')}</Text>
      <Button kind="quiet" title={t('firstRun.leaveForNow')} style={styles.startQuiet} onPress={() => router.replace('/')} />
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: {
    flex: 1,
    gap: 18,
    padding: theme.space.xl,
    paddingTop: theme.space.xl + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  start: { alignSelf: 'flex-start' },
  startQuiet: { alignSelf: 'flex-start', paddingHorizontal: 0 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  flex: { flex: 1 },
  regular: { fontWeight: '400' },
}));
