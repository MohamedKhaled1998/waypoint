import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { usePlanSummaries } from '../../plan/usePlan';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

/** At most this many Aspirations are active at once; they share one Time budget. */
const MAX_ASPIRATIONS = 3;

export default function ProgressScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { plans } = usePlanSummaries();
  const full = plans.length >= MAX_ASPIRATIONS;
  return (
    <ScrollView contentContainerStyle={styles.content}>
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
      {plans.length === 0 ? (
        <EmptyState title={t('progress.emptyTitle')} body={t('progress.emptyBody')} />
      ) : (
        plans.map((p) => (
          <Card key={p.aspirationId} hue={p.hue === 'coral' || p.hue === 'teal' || p.hue === 'amber' ? p.hue : undefined}>
            <Text variant="heading">{p.shortName}</Text>
          </Card>
        ))
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  content: { padding: theme.space.lg, gap: theme.space.md },
  add: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    padding: theme.space.md, borderRadius: theme.radii.md, borderWidth: 1, borderColor: theme.colors.border, backgroundColor: theme.colors.surface,
  },
  addFull: { opacity: 0.6 },
}));
