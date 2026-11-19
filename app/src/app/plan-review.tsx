import { useEffect, useState } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { TALK_IT_THROUGH_ROUTE, changeRows, confirmFacts, confirmModel, landingTabFor, tabBar, tabsFor, type PlanTab, type PlanVersionView } from '../plan/review';
import { usePlanReview } from '../plan/usePlan';
import { askedFlag, phonePermissions } from '../reminders/native';
import { askOnceAfterFirstPlan } from '../reminders/permissions';
import { rearmReminders } from '../reminders/rearm';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';

export default function PlanReviewScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { id } = useLocalSearchParams<{ id: string }>();
  const review = usePlanReview(String(id));
  const [chosen, setChosen] = useState<PlanTab | null>(null);
  const [confirming, setConfirming] = useState(false);
  const version = review.version;
  const tabs = tabsFor(version);
  const tab: PlanTab = chosen && tabs.includes(chosen) ? chosen : landingTabFor(version);
  const isRefresh = version?.reason === 'weekly_refresh';

  // Right after the first Plan is approved, once: notifications, then exact alarms, one at a time, each with its reason.
  const outcome = review.outcome;
  useEffect(() => {
    if (outcome === 'approved') void askOnceAfterFirstPlan(phonePermissions, askedFlag).then(() => rearmReminders()).catch(() => console.warn('reminders: arming failed'));
  }, [outcome]);

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

  return (
    <View style={styles.container}>
      <Stack.Screen options={{ title: t('plan.title') }} />
      <ScrollView contentContainerStyle={styles.pad}>
        {review.outcome === 'redrafting' ? <Banner message={t('plan.redrafting')} /> : null}
        {review.outcome === 'queued' ? <Banner message={t('plan.approveQueued')} /> : null}
        {review.outcome === 'replaced' ? <Banner message={t('plan.replaced')} /> : null}
        {review.outcome === 'failed' ? <Banner message={t('plan.approveFailed')} /> : null}
        <Card hue={version.hue}>
          <Text variant="label" muted>{t('plan.whatChanged')}</Text>
          <Text>{version.rationale}</Text>
        </Card>

        <View style={styles.tabs} accessibilityRole="tablist">
          {tabBar(review.unopened, tabs).map(({ tab: name, dot }) => (
            <Pressable
              key={name}
              accessibilityRole="tab"
              accessibilityState={{ selected: tab === name }}
              onPress={() => show(name)}
              style={[styles.tab, tab === name && styles.tabOn]}
            >
              <Text variant="label" style={tab === name ? styles.tabOnText : undefined}>{t(`plan.tabs.${name}`)}</Text>
              {dot ? (
                <View accessibilityLabel={t('plan.unopened')} style={styles.dot} />
              ) : null}
            </Pressable>
          ))}
        </View>

        {version.autoApplied ? <Banner message={t('plan.changes.autoApplied')} /> : null}
        {tab === 'changes' ? <Changes version={version} /> : null}
        {tab === 'milestones' ? <Route version={version} /> : null}
        {tab === 'route' ? <Route version={version} /> : null}
        {tab === 'twoWeeks' ? <TwoWeeks version={version} /> : null}
        {tab === 'habits' ? <Habits version={version} /> : null}
        {tab === 'assumes' ? <Assumes version={version} /> : null}
      </ScrollView>

      <View style={styles.bar}>
        {approved ? (
          <>
            <Text variant="heading">{t(isRefresh ? 'plan.changes.applied' : 'plan.started')}</Text>
            <Button title={t('plan.close')} onPress={() => router.replace('/')} />
          </>
        ) : confirming ? (
          <>
            <Text variant="heading">{t(isRefresh ? 'plan.changes.confirmTitle' : 'plan.confirmTitle')}</Text>
            <Text muted>
              {isRefresh
                ? facts.staysInHistory !== null
                  ? t('plan.changes.confirmBody', { n: facts.staysInHistory })
                  : t('plan.changes.confirmBodyPlain')
                : t('plan.confirmBody')}
            </Text>
            {confirm.lookAt ? (
              <>
                <Text muted>{t('plan.notOpened', { tabs: confirm.unopenedTabs.map((n) => t(`plan.tabs.${n}`)).join(', ') })}</Text>
                <Button kind="secondary" title={t('plan.lookAt', { tab: t(`plan.tabs.${confirm.lookAt}`) })} onPress={() => show(confirm.lookAt!)} />
              </>
            ) : null}
            <Button title={t(isRefresh ? 'plan.changes.apply' : 'plan.start')} disabled={review.approving} onPress={() => void approve()} />
            <Button kind="quiet" title={t('plan.notYet')} onPress={() => setConfirming(false)} />
          </>
        ) : (
          <>
            <Button title={t('plan.approve')} disabled={review.approving} onPress={() => setConfirming(true)} />
            <Button kind="quiet" title={t('plan.talkItThrough')} onPress={() => router.replace(TALK_IT_THROUGH_ROUTE)} />
          </>
        )}
      </View>
    </View>
  );
}

function Changes({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  const rows = changeRows(version);
  return (
    <View style={styles.section}>
      {rows.changes.length === 0 ? <Text muted>{t('plan.changes.nothingChanged')}</Text> : null}
      {rows.changes.map((c) => (
        <Card key={`${c.title}-${c.field}`}>
          <Text variant="heading">{c.title}</Text>
          <Text>
            <Text style={styles.struck}>{t(`plan.changes.field.${c.field}`, { value: c.before })}</Text>
            {' → '}
            {t(`plan.changes.field.${c.field}`, { value: c.after })}
          </Text>
          <Text muted>{c.reason}</Text>
        </Card>
      ))}
      <Text variant="label" muted>{t('plan.changes.stays')}</Text>
      {rows.stays.map((s) => (
        <Text key={`${s.what}-${s.title}`}>{t(s.what === 'milestone' ? 'plan.changes.stayMilestone' : 'plan.changes.stayHabit', { title: s.title, detail: s.detail })}</Text>
      ))}
    </View>
  );
}

function Route({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  return (
    <View style={styles.section}>
      {version.milestones.map((m) => (
        <Card key={m.id}>
          <Text variant="label" muted>{m.date}</Text>
          <Text variant="heading">{m.title}</Text>
          <Text muted>{t('plan.reachedWhen', { text: m.reachedWhen })}</Text>
          <Text variant="caption" muted>{t(`plan.kind.${m.kind}`)}</Text>
          {version.tasks.filter((x) => x.milestone === m.title).map((x) => (
            <Text key={x.id}>{t('plan.task', { title: x.title, minutes: x.minutes, due: x.due ?? t('plan.thisWeek') })}</Text>
          ))}
        </Card>
      ))}
    </View>
  );
}

const dateOf = (a: PlanVersionView['twoWeeks'][number]) => a.date;

function TwoWeeks({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  const days = Array.from(new Set(version.twoWeeks.map(dateOf))).sort();
  return (
    <View style={styles.section}>
      <Text muted>{t('plan.nothingScheduled')}</Text>
      {days.map((day) => (
        <Card key={day}>
          <Text variant="label" muted>{day}</Text>
          {version.twoWeeks.filter((a) => a.date === day).map((a) => (
            <Text key={`${a.date}-${a.action}-${a.slot}`}>{t('plan.action', { title: a.action, slot: a.slot, minutes: a.minutes })}</Text>
          ))}
        </Card>
      ))}
      <Text muted>{version.ramp}</Text>
    </View>
  );
}

function Habits({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  return (
    <View style={styles.section}>
      {version.habits.map((h) => (
        <Card key={h.id}>
          <Text variant="heading">{h.title}</Text>
          <Text muted>{t('plan.habit', { count: h.timesPerWeek, minutes: h.minutes, slots: h.slots.join(', ') })}</Text>
        </Card>
      ))}
    </View>
  );
}

function Assumes({ version }: { version: PlanVersionView }) {
  const { t } = useTranslation();
  return (
    <View style={styles.section}>
      {version.assumptions.length === 0 ? <Text muted>{t('plan.noAssumptions')}</Text> : null}
      {version.assumptions.map((a) => (
        <Card key={a}>
          <Text>{a}</Text>
        </Card>
      ))}
      <Text muted>{t('plan.correctAssumption')}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  pad: { padding: theme.space.lg, gap: theme.space.lg },
  section: { gap: theme.space.md },
  tabs: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  tab: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.xs,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  tabOn: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  tabOnText: { color: theme.colors.onAccent },
  struck: { textDecorationLine: 'line-through' },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: theme.colors.danger },
  bar: {
    gap: theme.space.sm,
    padding: theme.space.lg,
    borderTopWidth: 1,
    borderTopColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
}));
