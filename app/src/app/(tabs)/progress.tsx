import { useRouter } from 'expo-router';
import { Pressable, ScrollView, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { usePlanSummaries } from '../../plan/usePlan';
import { progressRows } from '../../pause/pause';
import { usePauses } from '../../pause/usePauses';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';
import type { Hue } from '../../theme/tokens';

const hueOf = (hue: string): Hue | undefined => (hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined);

export default function ProgressScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  if (plans.length === 0) return <EmptyState title={t('progress.emptyTitle')} body={t('progress.emptyBody')} />;
  return (
    <ScrollView contentContainerStyle={styles.content}>
      {progressRows(plans, paused.state).map((row) => (
        <Pressable
          key={row.aspirationId}
          accessibilityRole="button"
          onPress={() => router.push({ pathname: '/aspiration', params: { id: row.aspirationId } })}
        >
          <Card hue={row.paused ? undefined : hueOf(row.hue)} style={row.paused ? styles.dashed : undefined}>
            <Text variant="heading">{row.shortName}</Text>
            {row.paused ? (
              <View style={styles.paused}>
                <Text muted>{row.paused.until ? t('pause.until', { date: row.paused.until }) : t('pause.open')}</Text>
                <Button kind="secondary" title={t('pause.resume')} onPress={() => void paused.resume(row.paused!.pauseId)} />
              </View>
            ) : null}
          </Card>
        </Pressable>
      ))}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  content: { padding: theme.space.lg, gap: theme.space.md },
  dashed: { borderStyle: 'dashed', backgroundColor: 'transparent' },
  paused: { gap: theme.space.sm, marginTop: theme.space.sm },
}));
