import { useState } from 'react';
import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import type { Hue } from '../../theme/tokens';
import { ListRow } from '../../ui/ListRow';
import { Sheet } from '../../ui/Sheet';
import { useSession } from '../../auth/SessionProvider';
import { buildHabits, type DotState, type HabitRow } from '../../aspiration/habits';
import { httpLifecycleApi } from '../../lifecycle/api';
import { confirmFor, consistencyLine, menuItems, type MenuItem, type Work } from '../../lifecycle/lifecycle';
import { progressRows } from '../../pause/pause';
import { usePauses } from '../../pause/usePauses';
import { usePlanSummaries } from '../../plan/usePlan';
import { MeasureTab, useHasMeasure } from '../../measure/MeasureTab';
import { coachRoute, settleOffer } from '../../progress/progress';
import { PaceText, ProjectionText, RouteEnd, RouteStep, SettleCard, StatePill, WeekDots, YouAreHere } from '../../progress/ProgressParts';
import { useAspirationProgress } from '../../progress/useProgress';
import { actionById, localNow, useToday } from '../../today/useToday';
import { Banner } from '../../ui/Banner';
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
  const { theme } = useUnistyles();
  const hasMeasure = useHasMeasure(id ?? '');
  const [asking, setAsking] = useState<{ confirm: NonNullable<ReturnType<typeof confirmFor>>; act: (choice: string) => void } | null>(null);
  const [celebrating, setCelebrating] = useState(false);
  const [failedWork, setFailedWork] = useState(false);
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
    work().then(then, () => setFailedWork(true));
  };
  // Each choice asks first where it changes or ends something; Delete suggests Pause instead and can't be undone.
  const pick = (item: MenuItem) => {
    const name = a.shortName;
    const confirm = confirmFor(item, Boolean(row?.paused) || status === 'closed');
    const act = (choice: string) => {
      if (item === 'move_to_maintenance') run(() => api.moveToMaintenance(id), () => router.replace('/(tabs)/progress'));
      else if (item === 'mark_reached') run(() => api.markReached(id, choice === 'keepUp'), () => setCelebrating(true));
      else if (item === 'delete') run(() => api.remove(id), () => router.replace('/(tabs)/progress'));
    };
    if (item === 'pause') setMenu('pause');
    else if (item === 'resume') row?.paused && void paused.resume(row.paused.pauseId);
    else if (item === 'new_chapter') run(() => api.newChapter(id), () => router.push('/interview'));
    else if (confirm) {
      setMenu('closed');
      setAsking({ confirm, act });
    }
  };
  const start = (until: Parameters<typeof paused.start>[1]) => {
    setMenu('closed');
    void paused.start({ aspirationId: id }, until);
  };
  const visibleTabs = tabsShown(hasMeasure, habits.length);
  const shownTab = visibleTabs.includes(tab) ? tab : 'route';
  const hueColor = theme.colors.hues[a.hue].solid;
  const settle = settleForLine(t, a.settleForPicture, a.settleForDate ? date(a.settleForDate) : null);
  const next = a.next;
  const youAreHereAt = hereIndex(a.milestones);

  return (
    <ScrollView style={styles.screen} contentContainerStyle={styles.content}>
      <Stack.Screen
        options={{
          title: a.shortName,
          headerRight: () => <HeaderButton icon="ellipsis-vertical" label={t('lifecycle.menu')} onPress={() => setMenu('open')} />,
        }}
      />

      {failedWork ? <Banner message={t('lifecycle.failed')} /> : null}
      <View style={styles.header}>
        {row?.paused ? (
          <View style={styles.pausedStrip}>
            <Ionicons name="pause-circle-outline" size={18} color={theme.colors.textMuted} />
            <Text variant="label" muted style={[styles.flex, styles.regular]}>
              {row.paused.until ? t('pause.until', { date: date(row.paused.until) }) : t('pause.open')}
            </Text>
            <Button kind="secondary" title={t('pause.resume')} onPress={() => void paused.resume(row.paused!.pauseId)} />
          </View>
        ) : null}
        <View style={styles.headRow}>
          <View style={[styles.bigDot, { backgroundColor: hueColor }]} />
          <Text variant="title" style={styles.flex}>{a.ownWords}</Text>
        </View>
        <Text variant="label" muted style={styles.regular}>
          {[t('plan.hopingFor', { date: date(a.hopedForDate) }), settle ? t('plan.settleFor', { what: settle }) : null].filter(Boolean).join(' · ')}
        </Text>
        {a.pace !== null ? (
          <View style={styles.headRow}>
            <PaceText pace={a.pace} />
            <Text variant="label" muted style={styles.regular}>{t(`progress.paceWords.${a.paceWords}`)}</Text>
          </View>
        ) : status !== 'closed' ? (
          <Text variant="label" muted style={styles.regular}>{t(`progress.paceWords.${a.paceWords}`)}</Text>
        ) : null}
      </View>

      {status === 'closed' ? null : a.consistency ? (
        <Card hue={a.hue}>
          <Text variant="caption" muted>{t('lifecycle.consistencyTitle')}</Text>
          <View style={styles.headRow}>
            <WeekDots kept={a.consistency.weeksKeptUp} of={a.consistency.ofWeeks} hue={a.hue} />
            <Text variant="label">{consistencyLine(a.consistency, t)}</Text>
          </View>
          <Text variant="label" muted style={styles.regular}>{t('lifecycle.maintenanceLine')}</Text>
        </Card>
      ) : (
        <Card tone="accent">
          <Text variant="caption" muted>{t('progress.whereYouStand')}</Text>
          {next ? (
            <View style={styles.nextRow}>
              <View style={styles.flexGap}>
                <Text style={styles.semibold}>{t('progress.next', { title: next.title })}</Text>
                <ProjectionText projection={next.projection} />
              </View>
              <StatePill state={a.state} />
            </View>
          ) : (
            <Text>{t(`progress.standing.${a.state}`)}</Text>
          )}
        </Card>
      )}
      {settleOffer(a) ? <SettleCard choice={a.settleChoice!} onPick={(option) => router.push(coachRoute(a.aspirationId, a.shortName, option))} /> : null}

      <View style={styles.tabs} accessibilityRole="tablist">
        {visibleTabs.map((name) => (
          <Pressable key={name} accessibilityRole="tab" accessibilityState={{ selected: shownTab === name }} onPress={() => setTab(name)}
            style={[styles.tab, shownTab === name && { borderBottomColor: hueColor }]}>
            <Text variant="label" style={shownTab === name ? undefined : styles.mutedText}>{t(`progress.tabs.${name}`)}</Text>
          </Pressable>
        ))}
      </View>

      {shownTab === 'route' ? (
        <View>
          {a.milestones.map((m, i) => (
            <View key={m.id}>
              {i === youAreHereAt ? <YouAreHere date={date(today)} hue={a.hue} /> : null}
              <RouteStep milestone={m} hue={a.hue} date={date(m.date)} last={false} />
            </View>
          ))}
          <RouteEnd label={t('progress.routeEnd', { date: date(a.hopedForDate) })} />
        </View>
      ) : shownTab === 'measure' ? (
        <MeasureTab aspirationId={a.aspirationId} hue={a.hue} />
      ) : shownTab === 'habits' ? (
        <Card>
          {habits.map((h, i) => (
            <HabitRowView key={h.title} habit={h} hue={a.hue} last={i === habits.length - 1}
              onReminder={() => void setHabitReminder(actionById(actions, h.sample.id)!, !h.reminderOn, { today })} />
          ))}
        </Card>
      ) : (
        <Card>
          {a.planVersionId ? (
            <ListRow
              icon="document-text-outline"
              title={t('progress.plan.version', { number: a.planNumber ?? 1 })}
              subtitle={t('progress.plan.versionSub')}
              onPress={() => router.push({ pathname: '/plan-review', params: { id: a.planVersionId! } })}
            />
          ) : (
            <ListRow icon="document-text-outline" title={t('progress.plan.noVersion')} />
          )}
          <ListRow icon="time-outline" title={t('progress.plan.share', { hours: a.shareHoursPerWeek })} subtitle={t('progress.plan.shareSub')}
            onPress={() => router.push('/settings')} last />
          <Text variant="label" muted style={[styles.regular, styles.planNote]}>{t('progress.plan.note')}</Text>
          <View style={styles.buttons}>
            <Button kind="secondary" icon="calendar-outline" title={t('progress.plan.changeDates')} onPress={() => router.push(coachRoute(a.aspirationId, a.shortName, 'change_dates'))} />
            <Button kind="secondary" icon="git-compare-outline" title={t('progress.plan.changePlan')} onPress={() => router.push(coachRoute(a.aspirationId, a.shortName, 'change_plan'))} />
          </View>
        </Card>
      )}

      {menu === 'open' ? (
        <Sheet onClose={() => setMenu('closed')} label={t('common.close')}>
          <Text variant="heading">{a.shortName}</Text>
          <View>
            {menuItems(status, Boolean(row?.paused)).map((item, i, all) => (
              <ListRow
                key={item}
                icon={MENU_ICONS[item]}
                tint={item === 'delete' ? theme.colors.warn : undefined}
                title={t(`lifecycle.items.${item}`)}
                subtitle={t(`lifecycle.sub.${item}`)}
                chevron={false}
                onPress={() => pick(item)}
                last={i === all.length - 1}
              />
            ))}
          </View>
        </Sheet>
      ) : null}
      {menu === 'pause' || menu === 'days' ? (
        <Sheet onClose={() => setMenu('closed')} label={t('common.close')}>
          <Text variant="heading">{t('pause.menuTitle', { name: a.shortName })}</Text>
          <Text variant="label" muted style={styles.regular}>{t('pause.sheetNote')}</Text>
          {menu === 'pause' ? (
            <View>
              <ListRow icon="moon-outline" title={t('pause.scopeToday')} chevron={false} onPress={() => start('today')} />
              <ListRow icon="calendar-outline" title={t('pause.scopeFewDays')} onPress={() => setMenu('days')} />
              <ListRow icon="pause-circle-outline" title={t('pause.scopeOpen')} chevron={false} onPress={() => start('open')} last />
            </View>
          ) : (
            <View style={styles.buttons}>
              {FEW_DAYS.map((days) => (
                <Button key={days} kind="secondary" title={t('pause.days', { count: days })} onPress={() => start({ days })} />
              ))}
            </View>
          )}
        </Sheet>
      ) : null}
      {asking ? (
        <Sheet onClose={() => setAsking(null)} label={t('common.close')}>
          <Text variant="heading">{t(`${asking.confirm.prefix}.title`, { name: a.shortName })}</Text>
          <Text>{t(`${asking.confirm.prefix}.body`, { name: a.shortName })}</Text>
          {asking.confirm.choices.map((choice) => (
            <Button
              key={choice}
              kind={asking.confirm.destructive && choice === 'confirm' ? 'danger' : choice === asking.confirm.choices[0] ? 'primary' : 'secondary'}
              title={t(`${asking.confirm.prefix}.${choice}`)}
              onPress={() => {
                setAsking(null);
                if (choice === 'pauseInstead') setMenu('pause');
                else asking.act(choice);
              }}
            />
          ))}
          <Button kind="quiet" title={t('common.close')} onPress={() => setAsking(null)} />
        </Sheet>
      ) : null}
      {celebrating ? (
        <Sheet onClose={() => router.replace('/(tabs)/progress')} label={t('common.close')}>
          <Text variant="title">{t('lifecycle.reachedDone')}</Text>
          <Text>{t('lifecycle.reachedDoneBody', { name: a.shortName })}</Text>
          <Button title={t('common.close')} onPress={() => router.replace('/(tabs)/progress')} />
        </Sheet>
      ) : null}
    </ScrollView>
  );
}

/** Where "You are here" sits on the Route. */
function hereIndex(milestones: { youAreHere: boolean }[]): number {
  return milestones.findIndex((m) => m.youAreHere);
}

/** Measure and Habits only when there is something in them. */
function tabsShown(hasMeasure: boolean | null, habitCount: number) {
  return tabs.filter((name) => {
    if (name === 'measure') return hasMeasure !== false;
    if (name === 'habits') return habitCount > 0;
    return true;
  });
}

const MENU_ICONS: Record<MenuItem, keyof typeof Ionicons.glyphMap> = {
  pause: 'pause-circle-outline',
  resume: 'play-circle-outline',
  move_to_maintenance: 'leaf-outline',
  new_chapter: 'chatbubble-ellipses-outline',
  mark_reached: 'trophy-outline',
  delete: 'trash-outline',
};

/** A habit: hue dot, title, where it sits, the last seven days as dots, and its reminder switch. */
function HabitRowView({ habit, hue, last, onReminder }: { habit: HabitRow; hue: Hue; last: boolean; onReminder: () => void }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  return (
    <View style={[styles.habit, last && styles.lastHabit]}>
      <View style={styles.headRow}>
        <View style={[styles.dot8, { backgroundColor: theme.colors.hues[hue].solid }]} />
        <Text style={[styles.flex, styles.semibold]}>{habit.title}</Text>
        <Text variant="caption" muted>{t('aspiration.habits.lastSeven', { count: habit.doneDays })}</Text>
      </View>
      <Text variant="label" muted style={styles.regular}>
        {habit.anchor
          ? t('aspiration.habits.anchor', { words: habit.anchor.words, time: habit.anchor.time })
          : t('aspiration.habits.slot', { slot: habit.slot ?? '' })}
        {habit.targetMinutes ? ` · ${t('aspiration.habits.target', { count: habit.targetMinutes })}` : ''}
      </Text>
      <View style={styles.between}>
        <View style={styles.dots}>
          {habit.dots.map((d) => (
            <View key={d.date} accessibilityLabel={t(`aspiration.habits.dot.${d.state}`)} style={[styles.dot, dotStyle(d.state)]} />
          ))}
        </View>
        <Button kind="quiet" icon={habit.reminderOn ? 'notifications-outline' : 'notifications-off-outline'}
          title={t(habit.reminderOn ? 'aspiration.habits.reminderOn' : 'aspiration.habits.reminderOff')} onPress={onReminder} style={styles.reminder} />
      </View>
    </View>
  );
}

const dotStyle = (state: DotState) => styles[`dot_${state}`];

const styles = StyleSheet.create((theme) => ({
  screen: { backgroundColor: theme.colors.background },
  content: { padding: theme.space.lg, paddingBottom: theme.space.xl, gap: 14 },
  header: { gap: theme.space.xs },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  between: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' },
  nextRow: { flexDirection: 'row', alignItems: 'flex-start', gap: theme.space.sm },
  flex: { flex: 1 },
  flexGap: { flex: 1, gap: 2 },
  regular: { fontWeight: '400' },
  semibold: { fontWeight: '600' },
  mutedText: { color: theme.colors.textMuted },
  bigDot: { width: 12, height: 12, borderRadius: 6 },
  dot8: { width: 8, height: 8, borderRadius: 4 },
  pausedStrip: {
    flexDirection: 'row', alignItems: 'center', gap: theme.space.sm, padding: 10, marginBottom: theme.space.xs,
    borderRadius: theme.radii.md, borderWidth: 1, borderStyle: 'dashed', borderColor: theme.colors.skipped,
  },
  tabs: { flexDirection: 'row', gap: theme.space.lg, borderBottomWidth: 1, borderBottomColor: theme.colors.border },
  tab: { paddingVertical: theme.space.sm, borderBottomWidth: 2, borderBottomColor: 'transparent', marginBottom: -1 },
  buttons: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  planNote: { marginTop: theme.space.sm },
  habit: { gap: 6, paddingVertical: 10, borderBottomWidth: 1, borderBottomColor: theme.colors.border },
  lastHabit: { borderBottomWidth: 0 },
  reminder: { minHeight: 32, paddingHorizontal: 0, paddingVertical: 2 },
  dots: { flexDirection: 'row', gap: 6 },
  dot: { width: 12, height: 12, borderRadius: 6, borderWidth: 1.5, borderColor: theme.colors.border },
  dot_done: { backgroundColor: theme.colors.done, borderColor: theme.colors.done },
  dot_partly: { backgroundColor: `${theme.colors.partly}55`, borderColor: theme.colors.partly },
  dot_skipped: { backgroundColor: theme.colors.surfaceAlt, borderColor: theme.colors.skipped },
  dot_missed: { borderColor: theme.colors.warn },
  dot_open: { borderColor: theme.colors.accent },
  dot_none: { opacity: 0.3 },
}));
