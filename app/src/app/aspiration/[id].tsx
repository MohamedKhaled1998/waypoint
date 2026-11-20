import { useState } from 'react';
import { Alert, Pressable, ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useSession } from '../../auth/SessionProvider';
import { buildHabits, type DotState, type HabitRow } from '../../aspiration/habits';
import { httpLifecycleApi } from '../../lifecycle/api';
import { confirmFor, consistencyLine, menuItems, type MenuItem, type Work } from '../../lifecycle/lifecycle';
import { progressRows } from '../../pause/pause';
import { usePauses } from '../../pause/usePauses';
import { usePlanSummaries } from '../../plan/usePlan';
import { MeasureTab } from '../../measure/MeasureTab';
import { coachRoute, settleOffer } from '../../progress/progress';
import { RouteStep, SettleCard, StatePill } from '../../progress/ProgressParts';
import { useAspirationProgress } from '../../progress/useProgress';
import { actionById, localNow, useToday } from '../../today/useToday';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { HeaderButton } from '../../ui/HeaderButton';
import { EmptyState } from '../../ui/Screen';
import { Text } from '../../ui/Text';
import { formatDay } from '../../i18n/dates';
import { settleForLine } from '../../aspiration/settleFor';

const tabs = ['route', 'measure', 'habits', 'plan'] as const;
const FEW_DAYS = [2, 3, 5, 7] as const;

/**
 * One Aspiration: where it stands, its Route and Habits tabs, and the overflow menu that starts a Pause (three scopes).
 * A paused one shows a dashed Resume strip.
 */
export default function AspirationScreen() {
  const { t, i18n } = useTranslation();
  const { id } = useLocalSearchParams<{ id: string }>();
  const router = useRouter();
  const session = useSession();
  const { value: a, failed } = useAspirationProgress(id);
  const [tab, setTab] = useState<(typeof tabs)[number]>('route');
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const [menu, setMenu] = useState<'closed' | 'open' | 'pause' | 'days'>('closed');
  const row = progressRows(plans, paused.state).find((r) => r.aspirationId === id);
  const { actions, setHabitReminder } = useToday();
  const today = localNow().date;
  if (!a) return <EmptyState title={failed ? t('progress.loadFailed') : t('common.loading')} body="" />;
  const date = (iso: string) => formatDay(iso, { locale: i18n.language, weekday: false });
  const habits = buildHabits(actions, id ?? '', today);
  const status = a.status ?? 'active';
  const api = httpLifecycleApi(session);
  const run = (work: Work, then: () => void) => {
    setMenu('closed');
    work().then(then, () => Alert.alert(t('lifecycle.failed')));
  };
  // Each choice asks first where it changes or ends something; Delete suggests Pause instead and can't be undone.
  const pick = (item: MenuItem) => {
    const name = a.shortName;
    const confirm = confirmFor(item, Boolean(row?.paused) || status === 'closed');
    const act = (choice: string) => {
      if (item === 'move_to_maintenance') run(() => api.moveToMaintenance(id), () => router.replace('/(tabs)/progress'));
      else if (item === 'mark_reached') run(() => api.markReached(id, choice === 'keepUp'), () => router.replace('/(tabs)/progress'));
      else if (item === 'delete') run(() => api.remove(id), () => router.replace('/(tabs)/progress'));
    };
    if (item === 'pause') setMenu('pause');
    else if (item === 'resume') row?.paused && void paused.resume(row.paused.pauseId);
    else if (item === 'new_chapter') run(() => api.newChapter(id), () => router.push('/interview'));
    else if (confirm)
      Alert.alert(t(`${confirm.prefix}.title`, { name }), t(`${confirm.prefix}.body`, { name }), [
        { text: t('common.close'), style: 'cancel' },
        ...confirm.choices.map((choice) => ({
          text: t(`${confirm.prefix}.${choice}`),
          style: confirm.destructive && choice === 'confirm' ? ('destructive' as const) : ('default' as const),
          onPress: () => (choice === 'pauseInstead' ? setMenu('pause') : act(choice)),
        })),
      ]);
  };
  const start = (until: Parameters<typeof paused.start>[1]) => {
    setMenu('closed');
    void paused.start({ aspirationId: id }, until);
  };

  return (
    <ScrollView style={styles.screen} contentContainerStyle={styles.content}>
      <Stack.Screen
        options={{
          title: a.shortName,
          headerRight: () => <HeaderButton icon="ellipsis-horizontal" label={t('lifecycle.menu')} onPress={() => setMenu(menu === 'closed' ? 'open' : 'closed')} />,
        }}
      />
      {row?.paused ? (
        <Card style={styles.dashed}>
          <Text>{row.paused.until ? t('pause.until', { date: row.paused.until }) : t('pause.open')}</Text>
          <Button title={t('pause.resume')} onPress={() => void paused.resume(row.paused!.pauseId)} />
        </Card>
      ) : null}
      {menu === 'open' ? (
        <Card>
          <View style={styles.menu}>
            {menuItems(status, Boolean(row?.paused)).map((item) => (
              <Button key={item} kind="secondary" title={t(`lifecycle.items.${item}`)} onPress={() => pick(item)} />
            ))}
          </View>
        </Card>
      ) : null}
      {menu === 'pause' || menu === 'days' ? (
        <Card>
          <Text variant="label">{t('pause.menuTitle', { name: a.shortName })}</Text>
          {menu === 'pause' ? (
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
      <Card hue={a.hue} style={styles.header}>
        <Text variant="title">{a.ownWords}</Text>
        <Text muted>{t('progress.hoped', { date: date(a.hopedForDate) })}</Text>
        {a.settleForDate || a.settleForPicture ? (
          <Text muted>{t('progress.settle', { what: settleForLine(t, a.settleForPicture, a.settleForDate ? date(a.settleForDate) : null) })}</Text>
        ) : null}
        <Text>{t(`progress.paceWords.${a.paceWords}`)}</Text>
      </Card>
      {status === 'closed' ? null : a.consistency ? (
        <Card style={styles.stand}>
          <Text variant="label">{t('lifecycle.consistencyTitle')}</Text>
          <Text>{consistencyLine(a.consistency, t)}</Text>
          <Text muted>{t('lifecycle.maintenanceLine')}</Text>
        </Card>
      ) : (
        <Card style={styles.stand}>
          <Text variant="label">{t('progress.whereYouStand')}</Text>
          <Text>{t(`progress.standing.${a.state}`)}</Text>
          <StatePill state={a.state} />
        </Card>
      )}
      {settleOffer(a) ? <SettleCard choice={a.settleChoice!} onPick={(option) => router.push(coachRoute(a.aspirationId, a.shortName, option))} /> : null}
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
      ) : tab === 'habits' ? (
        <View style={styles.list}>
          {habits.length === 0 ? <Text muted>{t('aspiration.habits.empty')}</Text> : null}
          {habits.map((h) => (
            <HabitCard key={h.title} habit={h} onReminder={() => void setHabitReminder(actionById(actions, h.sample.id)!, !h.reminderOn, { today })} />
          ))}
        </View>
      ) : tab === 'plan' ? (
        <Card style={styles.stand}>
          <Text>{a.planVersionId ? t('progress.plan.version', { number: a.planNumber ?? 1 }) : t('progress.plan.noVersion')}</Text>
          {a.planVersionId ? <Button kind="secondary" title={t('plan.review')} onPress={() => router.push({ pathname: '/plan-review', params: { id: a.planVersionId! } })} /> : null}
          <Text muted>{t('progress.plan.share', { hours: a.shareHoursPerWeek })}</Text>
          <Button kind="secondary" title={t('progress.plan.changeDates')} onPress={() => router.push(coachRoute(a.aspirationId, a.shortName, 'change_dates'))} />
          <Button kind="secondary" title={t('progress.plan.changePlan')} onPress={() => router.push(coachRoute(a.aspirationId, a.shortName, 'change_plan'))} />
        </Card>
      ) : (
        <Text muted>{t('progress.laterTab')}</Text>
      )}
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
  screen: { backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, gap: theme.space.md },
  header: { gap: theme.space.xs },
  stand: { gap: theme.space.sm },
  tabs: { flexDirection: 'row', gap: theme.space.sm },
  tab: { paddingHorizontal: theme.space.md, paddingVertical: theme.space.sm, borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border },
  tabOn: { backgroundColor: theme.colors.accentTint },
  list: { gap: theme.space.md },
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
