import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { firstRun } from '../firstrun/native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import { usePauses } from '../pause/usePauses';
import { httpMyDataApi, type SlotRow } from '../settings/api';
import { shareExport } from '../settings/shareExport';
import { Chip, QuietSheet, SlotSheet, type SlotDraft } from '../settings/SettingsSheets';
import { quietHours, quietToTrack, slotHours, slotWindow, splitSegments, stepBudget, timeLabel, minutesOf } from '../settings/tracks';
import { useSettings } from '../settings/useSettings';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import { APPEARANCES, setAppearance, useAppearance } from '../theme/appearance';
import type { Hue } from '../theme/tokens';

const FREQUENCIES = ['Quiet', 'Gentle', 'Chatty'] as const;
const REFRESH_DAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'] as const;
const FEW_DAYS = [2, 3, 5, 7] as const;
const BUDGET_SETTLE_MS = 800;

type Editing = { slot: SlotRow | null } | null;

export default function SettingsScreen() {
  const { t } = useTranslation();
  const session = useSession();
  const { signOut } = session;
  const myData = useMemo(() => httpMyDataApi(session), [session]);
  const [exporting, setExporting] = useState(false);
  const router = useRouter();
  const { settings, failed, exactAlarms, api, run, allowExactAlarms } = useSettings();
  const appearance = useAppearance();
  const paused = usePauses(settings?.split ?? []);
  const [editing, setEditing] = useState<Editing>(null);
  const [quietOpen, setQuietOpen] = useState(false);
  const [fewDays, setFewDays] = useState(false);

  const exportData = async () => {
    setExporting(true);
    try {
      await shareExport(await myData.exportZip());
    } catch {
      Alert.alert(t('settings.myData.exportFailed'));
    } finally {
      setExporting(false);
    }
  };
  const confirmDelete = () =>
    Alert.alert(t('settings.myData.deleteTitle'), t('settings.myData.deleteBody'), [
      { text: t('settings.myData.exportFirst'), onPress: () => void exportData() },
      {
        text: t('settings.myData.deleteConfirm'),
        style: 'destructive',
        onPress: () => void session.deleteAccount(myData.deleteAccount).catch(() => Alert.alert(t('settings.myData.deleteFailed'))),
      },
      { text: t('settings.myData.cancel'), style: 'cancel' },
    ]);

  // The Time budget steps locally and is sent once the user stops tapping, so the Coach proposes one Re-plan, not one per tap.
  const [budget, setBudget] = useState<number | null>(null);
  const settle = useRef<ReturnType<typeof setTimeout> | null>(null);
  const shown = budget ?? settings?.requestedTimeBudgetHours ?? settings?.timeBudgetHours ?? null;
  useEffect(() => () => void (settle.current && clearTimeout(settle.current)), []);
  const step = (direction: 1 | -1) => {
    if (shown === null) return;
    const next = stepBudget(shown, direction);
    setBudget(next);
    if (settle.current) clearTimeout(settle.current);
    settle.current = setTimeout(() => {
      void run(api.setTimeBudget(next)).then(() => setBudget(null));
    }, BUDGET_SETTLE_MS);
  };

  if (!settings) {
    return <View style={styles.container}>{failed ? <Banner message={t('settings.loadFailed')} /> : null}</View>;
  }

  const total = settings.timeBudgetHours ?? 0;
  const segments = splitSegments(settings.split, shown ?? total);
  const slotTotal = settings.slots.reduce((sum, s) => sum + slotHours(s.days, slotWindow(s.start, s.end)), 0);
  const quiet = quietToTrack(settings.quietStart, settings.quietEnd);
  const pauseEverything = paused.state.everything;

  const saveSlot = (draft: SlotDraft) => {
    const slot = editing?.slot ?? null;
    setEditing(null);
    const body = { label: slot ? undefined : t('settings.slots.defaultLabel'), days: draft.days, start: draft.start, end: draft.end };
    void run(slot ? api.editSlot(slot.id, body) : api.addSlot(body));
  };

  return (
    <ScrollView contentContainerStyle={styles.container}>
      {failed ? <Banner message={t('settings.loadFailed')} /> : null}

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.budget.title')}</Text>
        <View style={styles.between}>
          <Button kind="secondary" title="−" accessibilityLabel={t('settings.budget.less')} onPress={() => step(-1)} />
          <Text variant="title">{t('settings.hoursPerWeek', { hours: shown ?? 0 })}</Text>
          <Button kind="secondary" title="+" accessibilityLabel={t('settings.budget.more')} onPress={() => step(1)} />
        </View>
        <View style={styles.bar}>
          {segments.map((s) => (
            <BarSegment key={s.aspirationId} hue={s.hue as Hue} fraction={s.fraction} />
          ))}
        </View>
        <View style={styles.row}>
          {settings.split.map((s) => (
            <Text key={s.aspirationId} variant="caption" muted>
              {s.shortName} · {t('settings.hoursPerWeek', { hours: s.hours })}
            </Text>
          ))}
        </View>
        {settings.requestedTimeBudgetHours !== null ? (
          <Text variant="caption">{t('settings.budget.asked', { hours: settings.requestedTimeBudgetHours })}</Text>
        ) : (
          <Text variant="caption" muted>{t('settings.budget.note')}</Text>
        )}
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.slots.title')}</Text>
        <Text muted>{t('settings.slots.summary', { slots: slotTotal, budget: total })}</Text>
        {settings.slots.map((s) => (
          <SlotLine key={s.id} slot={s} onPress={() => setEditing({ slot: s })} />
        ))}
        {settings.newSlots.map((s) => (
          <Text key={s.id} variant="caption">
            {t('settings.slots.fromRefresh', { days: s.days.map((d) => t(`settings.day.${d}`)).join(' '), start: timeLabel(minutesOf(s.start)), end: timeLabel(minutesOf(s.end)) })}
          </Text>
        ))}
        <Button kind="secondary" title={t('settings.slots.add')} onPress={() => setEditing({ slot: null })} />
        <Text variant="caption" muted>{t('settings.slots.applyNote')}</Text>
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.contact.title')}</Text>
        <View style={styles.row}>
          {FREQUENCIES.map((f) => (
            <Chip key={f} label={t(`settings.contact.${f}`)} selected={settings.contactFrequency === f} onPress={() => void run(api.setContactFrequency(f))} />
          ))}
        </View>
        <Text variant="caption" muted>{t('settings.contact.note')}</Text>
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.appearance.title')}</Text>
        <View style={styles.row}>
          {APPEARANCES.map((a) => (
            <Chip key={a} label={t(`settings.appearance.${a}`)} selected={appearance === a} onPress={() => setAppearance(a)} />
          ))}
        </View>
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.quiet.title')}</Text>
        <Pressable accessibilityRole="button" onPress={() => setQuietOpen(true)}>
          <Text>
            {t('settings.quiet.value', {
              start: settings.quietStart,
              end: settings.quietEnd,
              hours: quiet ? quietHours(quiet) : 0,
            })}
          </Text>
        </Pressable>
        <Text variant="caption" muted>{t('settings.quiet.note')}</Text>
      </Card>

      <Card style={[styles.card, exactAlarms === false && styles.warning]}>
        <Text variant="heading">{t('settings.reminders.title')}</Text>
        <Text>{exactAlarms === false ? t('settings.reminders.inexact') : t('settings.reminders.exact')}</Text>
        {exactAlarms === false ? (
          <>
            <Text variant="label">{t('settings.reminders.warning')}</Text>
            <Button title={t('settings.reminders.allow')} onPress={() => void allowExactAlarms()} />
          </>
        ) : null}
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.refresh.title')}</Text>
        <Text variant="label" muted>{t('settings.refresh.dayLabel')}</Text>
        <View style={styles.row}>
          {REFRESH_DAYS.map((d) => (
            <Chip
              key={d}
              label={t(`settings.day.${d.slice(0, 3)}`)}
              accessibilityLabel={t(`settings.dayLong.${d}`)}
              selected={settings.refreshDay === d}
              onPress={() => void run(api.setWeeklyRefresh(d, settings.refreshTime))}
            />
          ))}
        </View>
        <Text variant="label" muted>{t('settings.refresh.timeLabel')}</Text>
        <View style={styles.between}>
          <Button kind="secondary" title="−" accessibilityLabel={t('settings.refresh.earlier')} onPress={() => void run(api.setWeeklyRefresh(settings.refreshDay, shiftTime(settings.refreshTime, -30)))} />
          <Text variant="title">{settings.refreshTime}</Text>
          <Button kind="secondary" title="+" accessibilityLabel={t('settings.refresh.later')} onPress={() => void run(api.setWeeklyRefresh(settings.refreshDay, shiftTime(settings.refreshTime, 30)))} />
        </View>
        <Text variant="caption" muted>{t('settings.refresh.value', { day: t(`settings.dayLong.${settings.refreshDay}`), time: settings.refreshTime })}</Text>
      </Card>

      <Card style={[styles.card, pauseEverything && styles.dashed]}>
        <Text variant="heading">{t('settings.pause.title')}</Text>
        {pauseEverything ? (
          <>
            <Text>{pauseEverything.until ? t('settings.pause.pausedUntil', { date: pauseEverything.until }) : t('settings.pause.pausedOpen')}</Text>
            <Button title={t('pause.resume')} onPress={() => void paused.resume(pauseEverything.pauseId)} />
          </>
        ) : (
          <>
            <Text muted>{t('settings.pause.note')}</Text>
            <View style={styles.row}>
              <Chip label={t('settings.pause.today')} selected={false} onPress={() => void paused.start({}, 'today')} />
              <Chip label={t('settings.pause.fewDays')} selected={fewDays} onPress={() => setFewDays((v) => !v)} />
              <Chip label={t('settings.pause.open')} selected={false} onPress={() => void paused.start({}, 'open')} />
            </View>
            {fewDays ? (
              <View style={styles.row}>
                {FEW_DAYS.map((n) => (
                  <Chip key={n} label={t('settings.pause.days', { count: n })} selected={false} onPress={() => void paused.start({}, { days: n })} />
                ))}
              </View>
            ) : null}
          </>
        )}
      </Card>

      <Card style={styles.card}>
        <Button
          kind="secondary"
          title={t('settings.replayTour')}
          onPress={() => void firstRun.replayTour().then(() => router.navigate('/'))}
        />
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.myData.title')}</Text>
        <Text muted>{t('settings.myData.exportNote')}</Text>
        <Button kind="secondary" title={exporting ? t('settings.myData.exporting') : t('settings.myData.export')} onPress={() => void exportData()} />
        <Button kind="danger" title={t('settings.myData.delete')} onPress={confirmDelete} />
        <Text variant="label">{t('settings.myData.privacyTitle')}</Text>
        <Text variant="caption" muted>{t('settings.myData.privacyBackups')}</Text>
        <Text variant="caption" muted>{t('settings.myData.privacyProvider')}</Text>
      </Card>

      <Card style={styles.card}>
        <Text variant="heading">{t('settings.you.title')}</Text>
        <Text muted>{t('settings.you.google')}</Text>
        <Button kind="secondary" title={t('settings.signOut')} onPress={() => void signOut()} />
      </Card>

      {editing ? (
        <SlotSheet
          initial={editing.slot ? { label: editing.slot.label, days: editing.slot.days, start: editing.slot.start, end: editing.slot.end } : { label: '', days: ['mon'], start: '07:00', end: '08:00' }}
          onSave={saveSlot}
          onClose={() => setEditing(null)}
        />
      ) : null}
      {quietOpen ? (
        <QuietSheet
          start={settings.quietStart}
          end={settings.quietEnd}
          onSave={(start, end) => {
            setQuietOpen(false);
            void run(api.setQuietHours(start, end));
          }}
          onClose={() => setQuietOpen(false)}
        />
      ) : null}
    </ScrollView>
  );
}

function shiftTime(time: string, by: number): string {
  const total = (minutesOf(time) + by + 1440) % 1440;
  return timeLabel(total);
}

function BarSegment({ hue, fraction }: { hue: Hue; fraction: number }) {
  return <View style={[styles.segment(hue), { width: `${fraction * 100}%` }]} />;
}

function SlotLine({ slot, onPress }: { slot: SlotRow; onPress: () => void }) {
  const { t } = useTranslation();
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.slot}>
      <Text variant="label">{slot.label}</Text>
      <Text muted>
        {slot.days.map((d) => t(`settings.day.${d}`)).join(' ')} · {timeLabel(minutesOf(slot.start))}–{timeLabel(minutesOf(slot.end))}
      </Text>
      {slot.pending ? (
        <Text variant="caption">
          {t('settings.slots.fromRefresh', {
            days: slot.pending.days.map((d) => t(`settings.day.${d}`)).join(' '),
            start: timeLabel(minutesOf(slot.pending.start)),
            end: timeLabel(minutesOf(slot.pending.end)),
          })}
        </Text>
      ) : null}
    </Pressable>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { padding: theme.space.lg, paddingBottom: theme.space.lg + rt.insets.bottom, gap: theme.space.lg },
  card: { gap: theme.space.md },
  warning: { backgroundColor: theme.colors.warningTint },
  dashed: { borderStyle: 'dashed' },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, alignItems: 'center' },
  between: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: theme.space.md },
  bar: { flexDirection: 'row', height: 12, borderRadius: 6, overflow: 'hidden', backgroundColor: theme.colors.border },
  segment: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  slot: { gap: theme.space.xs, paddingVertical: theme.space.sm },
}));
