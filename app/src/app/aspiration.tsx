import { useState } from 'react';
import { ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { usePlanSummaries } from '../plan/usePlan';
import { progressRows } from '../pause/pause';
import { usePauses } from '../pause/usePauses';
import { buildHabits, type DotState, type HabitRow } from '../aspiration/habits';
import { actionById, localNow, useToday } from '../today/useToday';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { HeaderButton } from '../ui/HeaderButton';
import { Text } from '../ui/Text';

const FEW_DAYS = [2, 3, 5, 7] as const;

/** One Aspiration: its overflow menu starts a Pause (three scopes) and a paused one shows a dashed Resume strip. */
export default function AspirationScreen() {
  const { t } = useTranslation();
  const { id } = useLocalSearchParams<{ id: string }>();
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const [menu, setMenu] = useState<'closed' | 'open' | 'days'>('closed');
  const row = progressRows(plans, paused.state).find((r) => (r.aspirationId === id));
  const { actions, setHabitReminder } = useToday();
  const today = localNow().date;
  if (!row) return null;
  const habits = buildHabits(actions, id ?? '', today);
  const start = (until: Parameters<typeof paused.start>[1]) => {
    setMenu('closed');
    void paused.start({ aspirationId: row.aspirationId }, until);
  };
  return (
    <ScrollView contentContainerStyle={styles.content}>
      <Stack.Screen
        options={{
          title: row.shortName,
          headerRight: () => <HeaderButton icon="ellipsis-horizontal" label={t('pause.menu')} onPress={() => setMenu(menu === 'closed' ? 'open' : 'closed')} />,
        }}
      />
      {row.paused ? (
        <Card style={styles.dashed}>
          <Text>{row.paused.until ? t('pause.until', { date: row.paused.until }) : t('pause.open')}</Text>
          <Button title={t('pause.resume')} onPress={() => void paused.resume(row.paused!.pauseId)} />
        </Card>
      ) : null}
      {menu !== 'closed' && !row.paused ? (
        <Card>
          <Text variant="label">{t('pause.menuTitle', { name: row.shortName })}</Text>
          {menu === 'open' ? (
            <View style={styles.menu}>
              <Button kind="secondary" title={t('pause.scopeToday')} onPress={() => start('today')} />
              <Button kind="secondary" title={t('pause.scopeFewDays')} onPress={() => setMenu('days')} />
              <Button kind="secondary" title={t('pause.scopeOpen')} onPress={() => start('open')} />
            </View>
          ) : (
            <View style={styles.menu}>
              {FEW_DAYS.map((days) => (
                <Button key={days} kind="secondary" title={t('pause.days', { count: days })} onPress={() => start({ days })} />
              ))}
            </View>
          )}
        </Card>
      ) : null}
      <Text variant="heading">{t('aspiration.habits.tab')}</Text>
      {habits.length === 0 ? <Text muted>{t('aspiration.habits.empty')}</Text> : null}
      {habits.map((h) => (
        <HabitCard key={h.title} habit={h} onReminder={() => void setHabitReminder(actionById(actions, h.sample.id)!, !h.reminderOn, { today })} />
      ))}
    </ScrollView>
  );
}

function HabitCard({ habit, onReminder }: { habit: HabitRow; onReminder: () => void }) {
  const { t } = useTranslation();
  return (
    <Card style={styles.card}>
      <Text variant="label">{habit.title}</Text>
      <Text variant="caption" muted>
        {habit.anchor
          ? t('aspiration.habits.anchor', { words: habit.anchor.words, time: habit.anchor.time })
          : t('aspiration.habits.slot', { slot: habit.slot ?? '' })}
        {habit.targetMinutes ? ` · ${t('aspiration.habits.target', { count: habit.targetMinutes })}` : ''}
      </Text>
      <View style={styles.dots}>
        {habit.dots.map((d) => (
          <View key={d.date} accessibilityLabel={t(`aspiration.habits.dot.${d.state}`)} style={[styles.dot, dotStyle(d.state)]} />
        ))}
      </View>
      <Text variant="caption">{t('aspiration.habits.lastSeven', { count: habit.doneDays })}</Text>
      <Button kind="secondary" title={t(habit.reminderOn ? 'aspiration.habits.reminderOn' : 'aspiration.habits.reminderOff')} onPress={onReminder} />
    </Card>
  );
}

const dotStyle = (state: DotState) => styles[`dot_${state}`];

const styles = StyleSheet.create((theme) => ({
  content: { padding: theme.space.lg, gap: theme.space.md },
  dashed: { borderStyle: 'dashed', backgroundColor: 'transparent', gap: theme.space.md },
  menu: { gap: theme.space.sm, marginTop: theme.space.sm },
  card: { gap: theme.space.sm },
  dots: { flexDirection: 'row', gap: theme.space.sm },
  dot: { width: 14, height: 14, borderRadius: 7, borderWidth: 1, borderColor: theme.colors.border },
  dot_done: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  dot_partly: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
  dot_skipped: { backgroundColor: theme.colors.border },
  dot_missed: { borderColor: theme.colors.danger },
  dot_open: { borderColor: theme.colors.accent },
  dot_none: { opacity: 0.3 },
}));
