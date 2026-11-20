import Ionicons from '@expo/vector-icons/Ionicons';
import { useEffect, useState } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { formatDay, parseDay } from '../i18n/dates';
import { lowerFirst } from '../i18n/text';
import { TALK_IT_THROUGH_ROUTE, changeRows, confirmFacts, confirmModel, landingTabFor, tabBar, tabsFor, type PlanTab, type PlanVersionView } from '../plan/review';
import { usePlanReview } from '../plan/usePlan';
import { askedFlag, phonePermissions } from '../reminders/native';
import { askOnceAfterFirstPlan } from '../reminders/permissions';
import { firstRun, settlePermissions } from '../firstrun/native';
import { StepCounter } from '../ui/StepCounter';
import { useFirstRun } from '../firstrun/native';
import { rearmReminders } from '../reminders/rearm';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import { Sheet } from '../ui/Sheet';
import { asHue } from '../coach/CoachCards';
import { settleForLine } from '../aspiration/settleFor';
import { useAspirationProgress } from '../progress/useProgress';

export default function PlanReviewScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const router = useRouter();
  const { id, tab: askedTab } = useLocalSearchParams<{ id: string; tab?: string }>();
  const review = usePlanReview(String(id));
  // "See the week" on Today opens straight on Two weeks.
  const [chosen, setChosen] = useState<PlanTab | null>((askedTab as PlanTab | undefined) ?? null);
  const [confirming, setConfirming] = useState(false);
  const version = review.version;
  const tabs = tabsFor(version);
  const tab: PlanTab = chosen && tabs.includes(chosen) ? chosen : landingTabFor(version);
  const isRefresh = version?.reason === 'weekly_refresh';

  // Right after the first Plan is approved, once: notifications, then exact alarms, one at a time, each with its reason.
  const outcome = review.outcome;
  useEffect(() => {
    if (outcome !== 'approved') return;
    void firstRun.planApproved().then(() => settlePermissions()).then(() => {
      if (firstRun.getState().step === 'tourTry') router.replace('/');
    });
  }, [outcome, router]);
  const { step: firstRunStep } = useFirstRun();
  // Opening the draft from Today after leaving the wait is still the first Plan's review.
  useEffect(() => {
    if (firstRunStep === 'drafting') void firstRun.advance('planReview');
  }, [firstRunStep]);

  // Opened on a tab from elsewhere: that tab counts as looked at.
  useEffect(() => {
    if (askedTab && review.version) review.open(askedTab as PlanTab);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [askedTab, !!review.version]);

  const show = (next: PlanTab) => {
    setChosen(next);
    review.open(next);
    setConfirming(false);
  };

  const approve = async () => {
    await review.approve();
    setConfirming(false);
  };

  if (!version) {
    return (
      <View style={styles.container}>
        <Stack.Screen options={{ title: t('plan.title') }} />
        <Text muted style={styles.pad}>{review.loadFailed ? t('plan.loadFailed') : t('common.loading')}</Text>
      </View>
    );
  }

  const confirm = confirmModel(review.unopened, tabs);
  const facts = confirmFacts(version);
  const approved = review.outcome === 'approved';
  const first = version.reason === 'first_plan';
  const decided = !approved && version.status !== 'proposed';
  const confirmTitle = first ? t('plan.confirmTitle') : isRefresh ? t('plan.changes.confirmTitle') : t('plan.switchTitle', { n: (version.replacesNumber ?? 0) + 1 });
  const confirmBody = first ? t('plan.confirmBody') : isRefresh ? t('plan.changes.confirmBodyShort') : t('plan.switchBody');

  return (
    <View style={styles.container}>
      <Stack.Screen options={{ title: t('plan.title') }} />
      {firstRunStep === 'drafting' || firstRunStep === 'planReview' || firstRunStep === 'permissions' ? <View style={styles.counter}><StepCounter step={firstRunStep} /></View> : null}
      <PlanHeader version={version} first={first} isRefresh={isRefresh} />
      <ScrollView contentContainerStyle={styles.pad}>
        {review.outcome === 'redrafting' ? <Banner message={t('plan.redrafting')} /> : null}
        {review.outcome === 'queued' ? <Banner message={t('plan.approveQueued')} /> : null}
        {review.outcome === 'replaced' ? <Banner message={t('plan.replaced')} /> : null}
        {review.outcome === 'failed' ? <Banner message={t('plan.approveFailed')} /> : null}
        <Card tone="accent">
          <Text variant="caption" muted>{t(first ? 'plan.whyRoute' : 'plan.whatChanged')}</Text>
          <Text>{version.rationale}</Text>
        </Card>

        <View style={styles.segments} accessibilityRole="tablist">
          {tabBar(review.unopened, tabs).map(({ tab: name, dot }) => (
            <Pressable
              key={name}
              accessibilityRole="tab"
              accessibilityState={{ selected: tab === name }}
              onPress={() => show(name)}
              style={[styles.segment, tab === name && styles.segmentOn]}
            >
              <Text variant="label" numberOfLines={1} style={tab === name ? styles.segmentOnText : styles.segmentText}>{t(`plan.tabs.${name}`)}</Text>
              {dot && !decided ? <View accessibilityLabel={t('plan.unopened')} style={styles.dot} /> : null}
            </Pressable>
          ))}
        </View>

        {version.autoApplied ? <Banner message={t('plan.changes.autoApplied')} /> : null}
        {tab === 'changes' ? <Changes version={version} /> : null}
        {tab === 'milestones' || tab === 'route' ? <Route version={version} /> : null}
        {tab === 'twoWeeks' ? <TwoWeeks version={version} proposed={version.status === 'proposed'} /> : null}
        {tab === 'habits' ? <Habits version={version} /> : null}
        {tab === 'assumes' ? <Assumes version={version} /> : null}
        {confirm.unopenedTabs.length > 0 && !decided && !approved ? (
          <Text variant="caption" muted style={styles.centered}>{t('plan.notOpenedLine', { tabs: confirm.unopenedTabs.map((n) => t(`plan.tabs.${n}`)).join(' · ') })}</Text>
        ) : null}
      </ScrollView>

      <View style={styles.bar}>
        {decided ? (
          // Opened from the thread or the Aspiration: a version already decided has nothing to approve.
          <>
            <Text muted>{t(version.status === 'approved' ? 'plan.inForce' : 'plan.notInForce')}</Text>
            <Button kind="secondary" title={t('plan.close')} onPress={() => router.back()} />
          </>
        ) : approved ? (
          <>
            <Text variant="heading">{t(isRefresh ? 'plan.changes.applied' : 'plan.started')}</Text>
            <Button title={t('plan.close')} onPress={() => router.replace('/')} />
          </>
        ) : (
          <View style={styles.pair}>
            <View style={styles.half}>
              <Button kind="secondary" title={t('plan.talkItThrough')} onPress={() => router.replace(TALK_IT_THROUGH_ROUTE)} />
            </View>
            <View style={styles.half}>
              <Button icon="checkmark" title={t('plan.approve')} disabled={review.approving} onPress={() => setConfirming(true)} />
            </View>
          </View>
        )}
      </View>

      {confirming && !decided && !approved ? (
        <Sheet onClose={() => setConfirming(false)} label={t('plan.notYet')}>
          <Text variant="heading">{confirmTitle}</Text>
          <Text>{confirmBody}</Text>
          {confirm.lookAt ? (
            <View style={styles.unseen}>
              <Ionicons name="eye-off-outline" size={18} color={theme.colors.accent} />
              <Text variant="label" style={[styles.flex, styles.regular]}>{t('plan.notOpened', { tabs: confirm.unopenedTabs.map((n) => t(`plan.tabs.${n}`)).join(', ') })}</Text>
            </View>
          ) : null}
          <View style={styles.pair}>
            <View style={styles.half}>
              {confirm.lookAt ? (
                <Button kind="secondary" title={t('plan.lookAt', { tab: t(`plan.tabs.${confirm.lookAt}`) })} onPress={() => show(confirm.lookAt!)} />
              ) : (
                <Button kind="secondary" title={t('plan.notYet')} onPress={() => setConfirming(false)} />
              )}
            </View>
            <View style={styles.half}>
              <Button icon="checkmark" title={t(first ? 'plan.startIt' : 'plan.approve')} disabled={review.approving} onPress={() => void approve()} />
            </View>
          </View>
          {!first && facts.staysInHistory !== null ? (
            <Text variant="caption" muted style={styles.centered}>{t('plan.staysInHistory', { n: facts.staysInHistory })}</Text>
          ) : null}
        </Sheet>
      ) : null}
    </View>
  );
}

/** Whose Plan this is, which version, and the dates it aims at. */
function PlanHeader({ version, first, isRefresh }: { version: PlanVersionView; first: boolean; isRefresh: boolean }) {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
  const { value: a } = useAspirationProgress(version.aspirationId);
  const hue = asHue(version.hue);
  const day = (iso: string) => formatDay(iso, { locale: i18n.language, weekday: false });
  const n = version.replacesNumber ?? (version.number ? version.number - 1 : null);
  const label = first ? t('plan.headFirst') : n ? t(isRefresh ? 'plan.headRefresh' : 'plan.headRePlan', { from: n, to: n + 1 }) : t('plan.headPlan');
  const settle = a ? settleForLine(t, a.settleForPicture, a.settleForDate ? day(a.settleForDate) : null) : undefined;
  return (
    <View style={styles.head}>
      <View style={styles.headRow}>
        <View style={[styles.hueDot, { backgroundColor: hue ? theme.colors.hues[hue].solid : theme.colors.accent }]} />
        <Text variant="caption" muted>{label}</Text>
      </View>
      <Text variant="title" style={styles.headTitle}>{a?.ownWords ?? version.shortName}</Text>
      {a ? (
        <Text variant="label" muted style={styles.regular}>
          {[t('plan.hopingFor', { date: day(a.hopedForDate) }), settle ? t('plan.settleFor', { what: settle }) : null, first ? null : t('plan.neitherChanges')].filter(Boolean).join(' · ')}
        </Text>
      ) : null}
    </View>
  );
}

function Changes({ version }: { version: PlanVersionView }) {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
  const rows = changeRows(version);
  return (
    <View style={styles.section}>
      {rows.changes.length === 0 ? <Text muted>{t('plan.changes.nothingChanged')}</Text> : null}
      {rows.changes.map((c) => (
        <View key={`${c.title}-${c.field}`} style={styles.changeRow}>
          <Ionicons name="swap-horizontal" size={18} color={theme.colors.accent} />
          <View style={styles.flexGap}>
            <Text variant="label" muted style={styles.regular}>{c.title}</Text>
            <View style={styles.beforeAfter}>
              <Text variant="label" muted style={[styles.regular, styles.struck]}>{t(`plan.changes.field.${c.field}`, { value: c.before })}</Text>
              <Ionicons name="arrow-forward" size={12} color={theme.colors.textMuted} />
              <Text style={styles.bold}>{t(`plan.changes.field.${c.field}`, { value: c.after })}</Text>
            </View>
            <Text variant="label" muted style={styles.regular}>{c.reason}</Text>
          </View>
        </View>
      ))}
      <Text variant="caption" muted>{t('plan.changes.stays')}</Text>
      {rows.stays.map((s) => (
        <View key={`${s.what}-${s.title}`} style={styles.headRow}>
          <Ionicons name="checkmark-circle-outline" size={14} color={theme.colors.done} />
          <Text variant="label" muted style={[styles.flex, styles.regular]}>
            {s.what === 'milestone'
              ? t('plan.changes.stayMilestone', { title: s.title, date: s.date ? formatDay(s.date, { locale: i18n.language }) : '' })
              : t('plan.changes.stayHabit', { title: s.title, count: s.timesPerWeek, minutes: s.minutes })}
          </Text>
        </View>
      ))}
    </View>
  );
}

/** The Milestones as a vertical timeline. */
function Route({ version }: { version: PlanVersionView }) {
  const { t, i18n } = useTranslation();
  return (
    <View>
      {version.milestones.map((m, i) => {
        const reached = m.state === 'reached';
        return (
          <View key={m.id} style={styles.step}>
            <View style={styles.rail}>
              <View style={[styles.node, reached && styles.nodeDone]} />
              {i < version.milestones.length - 1 ? <View style={styles.line} /> : null}
            </View>
            <View style={styles.stepBody}>
              <Text style={styles.semibold}>{m.title}</Text>
              <Text variant="label" muted style={styles.regular}>{t('plan.reachedWhen', { text: lowerFirst(m.reachedWhen) })}</Text>
              <Text variant="label" style={reached ? styles.doneText : styles.accentText}>{formatDay(m.date, { locale: i18n.language })}</Text>
              {version.tasks.filter((x) => x.milestone === m.title).map((x) => (
                <Text key={x.id} variant="label" muted style={styles.regular}>
                  {t('plan.task', { title: x.title, minutes: x.minutes, due: x.due ? formatDay(x.due, { locale: i18n.language }) : t('plan.thisWeek') })}
                </Text>
              ))}
            </View>
          </View>
        );
      })}
    </View>
  );
}

/** The two weeks ahead as a calendar: a hue dot per Action; tap a day for its Actions. */
function TwoWeeks({ version, proposed }: { version: PlanVersionView; proposed: boolean }) {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
  const days = fortnight(version.startDate);
  const firstBusy = firstBusyDay(days, version.twoWeeks);
  const [selected, setSelected] = useState(firstBusy);
  const hue = asHue(version.hue);
  const dotColor = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
  const day = days[selected]!;
  const actions = actionsOn(version.twoWeeks, day);
  return (
    <View style={styles.section}>
      {proposed ? <Text variant="label" muted style={styles.regular}>{t('plan.nothingScheduled')}</Text> : null}
      {[0, 7].map((start) => (
        <View key={start} style={styles.week}>
          {days.slice(start, start + 7).map((d, i) => {
            const idx = start + i;
            const on = idx === selected;
            const count = actionsOn(version.twoWeeks, d).length;
            const date = parseDay(d)!;
            return (
              <Pressable key={d} accessibilityRole="button" accessibilityState={{ selected: on }} accessibilityLabel={formatDay(d, { locale: i18n.language })}
                onPress={() => setSelected(idx)} style={[styles.dayCell, on && styles.dayCellOn]}>
                <Text variant="caption" style={on ? styles.onAccent : styles.mutedText}>
                  {new Intl.DateTimeFormat(i18n.language, { weekday: 'narrow' }).format(date)}
                </Text>
                <Text variant="label" style={[styles.bold, on ? styles.onAccent : undefined]}>{date.getDate()}</Text>
                <View style={styles.dots}>
                  {Array.from({ length: Math.min(count, 3) }, (_, j) => (
                    <View key={j} style={[styles.miniDot, { backgroundColor: on ? theme.colors.onAccent : dotColor }]} />
                  ))}
                </View>
              </Pressable>
            );
          })}
        </View>
      ))}
      <View style={styles.dayList}>
        <Text variant="caption" muted>{formatDay(day, { locale: i18n.language })}</Text>
        {actions.length === 0 ? <Text variant="label" muted style={styles.regular}>{t('plan.restDay')}</Text> : null}
        {actions.map((a) => (
          <View key={`${a.action}-${a.slot ?? a.anchor}`} style={styles.dayAction}>
            <View style={[styles.bar4, { backgroundColor: dotColor }]} />
            <Text variant="label" style={[styles.flex, styles.regular]}>{a.action}</Text>
            <Text variant="label" muted style={styles.regular}>
              {a.anchor ? a.anchor : t('plan.slotMinutes', { slot: a.slot ?? '', minutes: a.minutes ?? 0 })}
            </Text>
          </View>
        ))}
      </View>
      {version.ramp ? <Text variant="label" muted style={styles.regular}>{version.ramp}</Text> : null}
    </View>
  );
}

function Habits({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  return (
    <View style={styles.section}>
      {version.habits.map((h) => (
        <View key={h.id} style={styles.headRow}>
          <Ionicons name="repeat" size={16} color={theme.colors.textMuted} />
          <View style={styles.flex}>
            <Text>{h.title}</Text>
            <Text variant="label" muted style={styles.regular}>
              {h.anchor
                ? t('plan.anchorHabit', { count: h.timesPerWeek, anchor: h.anchor.words, time: h.anchor.time })
                : t('plan.habit', { count: h.timesPerWeek, minutes: h.minutes, slots: h.slots.join(', ') })}
            </Text>
          </View>
        </View>
      ))}
    </View>
  );
}

function Assumes({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  return (
    <View style={styles.section}>
      <Text variant="caption" muted>{t('plan.assumesTitle')}</Text>
      {version.assumptions.length === 0 ? <Text muted>{t('plan.noAssumptions')}</Text> : null}
      {version.assumptions.map((a) => (
        <Text key={a} variant="label" style={styles.regular}>{t('plan.bullet', { text: a })}</Text>
      ))}
      <Text variant="label" muted style={styles.regular}>{t('plan.correctAssumption')}</Text>
    </View>
  );
}

function actionsOn<T extends { date: string }>(actions: T[], day: string): T[] {
  return actions.filter((a) => a.date === day);
}

/** The first day of the window with something on it, or the first day. */
function firstBusyDay(days: string[], actions: { date: string }[]): number {
  return Math.max(0, days.findIndex((d) => actionsOn(actions, d).length > 0));
}

/** Fourteen days from the window's start, as yyyy-MM-dd. */
function fortnight(start: string): string[] {
  const d = parseDay(start);
  if (!d) return [];
  const p = (n: number) => String(n).padStart(2, '0');
  return Array.from({ length: 14 }, (_, i) => {
    const x = new Date(d.getFullYear(), d.getMonth(), d.getDate() + i, 12);
    return `${x.getFullYear()}-${p(x.getMonth() + 1)}-${p(x.getDate())}`;
  });
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  counter: { paddingHorizontal: theme.space.lg, paddingTop: theme.space.sm },
  pad: { paddingHorizontal: theme.space.lg, paddingBottom: theme.space.xl, gap: 14 },
  head: { gap: theme.space.xs, paddingHorizontal: theme.space.lg, paddingTop: theme.space.sm, paddingBottom: theme.space.md },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  headTitle: { fontSize: 24, lineHeight: 30 },
  hueDot: { width: 8, height: 8, borderRadius: 4 },
  section: { gap: 10 },
  flex: { flex: 1 },
  flexGap: { flex: 1, gap: 2 },
  regular: { fontWeight: '400' },
  bold: { fontWeight: '700' },
  semibold: { fontWeight: '600' },
  struck: { textDecorationLine: 'line-through' },
  centered: { textAlign: 'center' },
  accentText: { color: theme.colors.accent },
  doneText: { color: theme.colors.done },
  mutedText: { color: theme.colors.textMuted },
  onAccent: { color: theme.colors.onAccent },
  segments: { flexDirection: 'row', padding: 3, borderRadius: theme.radii.pill, backgroundColor: theme.colors.surfaceAlt },
  segment: { flex: 1, flexDirection: 'row', justifyContent: 'center', alignItems: 'center', gap: 4, paddingVertical: theme.space.sm, borderRadius: theme.radii.pill },
  segmentOn: { backgroundColor: theme.colors.surface },
  segmentText: { color: theme.colors.textMuted, fontWeight: '500' },
  segmentOnText: { fontWeight: '700' },
  dot: { width: 6, height: 6, borderRadius: 3, backgroundColor: theme.colors.accent },
  changeRow: {
    flexDirection: 'row', alignItems: 'center', gap: theme.space.md, padding: theme.space.md,
    borderRadius: theme.radii.md, borderWidth: 1, borderColor: theme.colors.border, backgroundColor: theme.colors.surface,
  },
  beforeAfter: { flexDirection: 'row', alignItems: 'center', gap: 6, flexWrap: 'wrap' },
  step: { flexDirection: 'row', gap: theme.space.md },
  rail: { alignItems: 'center', width: 16 },
  node: { width: 14, height: 14, borderRadius: 7, marginTop: 3, borderWidth: 2, borderColor: theme.colors.accent, backgroundColor: theme.colors.background },
  nodeDone: { borderColor: theme.colors.done, backgroundColor: theme.colors.done },
  line: { width: 2, flex: 1, backgroundColor: theme.colors.border },
  stepBody: { flex: 1, gap: 2, paddingBottom: theme.space.lg },
  week: { flexDirection: 'row', gap: theme.space.xs },
  dayCell: {
    flex: 1, alignItems: 'center', gap: theme.space.xs, paddingVertical: theme.space.sm,
    borderRadius: theme.radii.md, borderWidth: 1, borderColor: theme.colors.border, backgroundColor: theme.colors.surface,
  },
  dayCellOn: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  dots: { flexDirection: 'row', gap: 3, height: 6 },
  miniDot: { width: 6, height: 6, borderRadius: 3 },
  dayList: { gap: 6, minHeight: 64 },
  dayAction: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  bar4: { width: 4, height: 18, borderRadius: 2 },
  unseen: { flexDirection: 'row', gap: theme.space.sm, padding: theme.space.md, borderRadius: theme.radii.md, backgroundColor: theme.colors.accentTint },
  pair: { flexDirection: 'row', gap: 10 },
  half: { flex: 1 },
  bar: {
    gap: theme.space.sm,
    padding: theme.space.lg,
    // Pinned to the bottom: clear the nav bar.
    paddingBottom: theme.space.lg + rt.insets.bottom,
    borderTopWidth: 1,
    borderTopColor: theme.colors.border,
    backgroundColor: theme.colors.background,
  },
}));
