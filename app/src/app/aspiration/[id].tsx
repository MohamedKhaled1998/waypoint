import { useState } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { useLocalSearchParams } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { MeasureTab } from '../../measure/MeasureTab';
import { RouteStep, StatePill } from '../../progress/ProgressParts';
import { useAspirationProgress } from '../../progress/useProgress';
import { Card } from '../../ui/Card';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';

const tabs = ['route', 'measure', 'habits', 'plan'] as const;

export default function AspirationScreen() {
  const { t, i18n } = useTranslation();
  const { id } = useLocalSearchParams<{ id: string }>();
  const { value: a, failed } = useAspirationProgress(id);
  const [tab, setTab] = useState<(typeof tabs)[number]>('route');
  if (!a) return <EmptyState title={failed ? t('progress.loadFailed') : t('common.loading')} body="" />;
  const date = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString(i18n.language, { day: 'numeric', month: 'long', year: 'numeric' });

  return (
    <ScrollView style={styles.screen} contentContainerStyle={styles.content}>
      <Card hue={a.hue} style={styles.header}>
        <Text variant="title">{a.ownWords}</Text>
        <Text muted>{t('progress.hoped', { date: date(a.hopedForDate) })}</Text>
        {a.settleForDate ? <Text muted>{t('progress.settle', { date: date(a.settleForDate) })}</Text> : null}
        <Text>{t(`progress.paceWords.${a.paceWords}`)}</Text>
      </Card>
      <Card style={styles.stand}>
        <Text variant="label">{t('progress.whereYouStand')}</Text>
        <Text>{t(`progress.standing.${a.state}`)}</Text>
        <StatePill state={a.state} />
      </Card>
      <View style={styles.tabs}>
        {tabs.map((name) => (
          <Pressable key={name} accessibilityRole="tab" accessibilityState={{ selected: tab === name }} onPress={() => setTab(name)} style={[styles.tab, tab === name ? styles.tabOn : null]}>
            <Text variant="label">{t(`progress.tabs.${name}`)}</Text>
          </Pressable>
        ))}
      </View>
      {tab === 'route' ? (
        <View>
          {a.milestones.map((m) => (
            <RouteStep key={m.id} milestone={m} hue={a.hue} date={date(m.date)} />
          ))}
        </View>
      ) : tab === 'measure' ? (
        <MeasureTab aspirationId={a.aspirationId} hue={a.hue} />
      ) : (
        <Text muted>{t('progress.laterTab')}</Text>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  screen: { backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, gap: theme.space.md },
  header: { gap: theme.space.xs },
  stand: { gap: theme.space.sm },
  tabs: { flexDirection: 'row', gap: theme.space.sm },
  tab: { paddingHorizontal: theme.space.md, paddingVertical: theme.space.sm, borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border },
  tabOn: { backgroundColor: theme.colors.accentTint },
}));
