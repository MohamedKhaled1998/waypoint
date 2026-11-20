import { useEffect, useMemo, useRef, useState } from 'react';
import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, ScrollView, View } from 'react-native';
import { useRouter } from 'expo-router';
import { firstRun } from '../firstrun/native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { formatDay } from '../i18n/dates';
import { ListRow } from '../ui/ListRow';
import { Section } from '../ui/Section';
import { Sheet } from '../ui/Sheet';
import { useSession } from '../auth/SessionProvider';
import { usePauses } from '../pause/usePauses';
import { httpMyDataApi, type NewSlotRow, type SlotRow } from '../settings/api';
import { shareExport } from '../settings/shareExport';
import { QuietSheet, SlotSheet, type SlotDraft } from '../settings/SettingsSheets';
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

type Editing = { slot: SlotRow | null; newSlot?: NewSlotRow } | null;

export default function SettingsScreen() {
  const { t, i18n } = useTranslation();
  const { theme } = useUnistyles();
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
  const [pauseOpen, setPauseOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleteFailed, setDeleteFailed] = useState(false);
  const [exportFailed, setExportFailed] = useState(false);

  const exportData = async () => {
    setExporting(true);
    try {
      await shareExport(await myData.exportZip());
    } catch {
      setExportFailed(true);
    } finally {
      setExporting(false);
    }
  };
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
    const fresh = editing?.newSlot ?? null;
    setEditing(null);
    const body = { label: slot || fresh ? undefined : t('settings.slots.defaultLabel'), days: draft.days, start: draft.start, end: draft.end };
    void run(slot ? api.editSlot(slot.id, body) : fresh ? api.editNewSlot(fresh.id, body) : api.addSlot(body));
  };

  const underBudget = slotTotal < total;
  const editingNew = editing?.newSlot ?? null;

  return (
    <ScrollView contentContainerStyle={styles.container}>
      {failed ? <Banner message={t('settings.loadFailed')} /> : null}
      {exportFailed ? <Banner message={t('settings.myData.exportFailed')} /> : null}

      <Section title={t('settings.budget.title')}>
        <Card>
          <View style={styles.between}>
            <Text variant="heading">{t('settings.hoursPerWeek', { hours: shown ?? 0 })}</Text>
            <View style={styles.steppers}>
              <Stepper icon="remove" label={t('settings.budget.less')} onPress={() => step(-1)} />
              <Stepper icon="add" label={t('settings.budget.more')} onPress={() => step(1)} />
            </View>
          </View>
          <View style={styles.bar}>
            {segments.map((s) => (
              <BarSegment key={s.aspirationId} hue={s.hue as Hue} fraction={s.fraction} />
            ))}
          </View>
          <View style={styles.legend}>
            {settings.split.map((s) => (
              <View key={s.aspirationId} style={styles.legendItem}>
                <View style={[styles.dot, { backgroundColor: theme.colors.hues[s.hue as Hue]?.solid ?? theme.colors.accent }]} />
                <Text variant="label" muted style={styles.regular}>{s.shortName} · {t('settings.hoursPerWeek', { hours: s.hours })}</Text>
              </View>
            ))}
          </View>
          {pauseEverything ? <Text variant="caption" muted>{t('settings.budget.pausedNote')}</Text> : null}
          {settings.requestedTimeBudgetHours === null ? <Text variant="caption" muted>{t('settings.budget.note')}</Text> : null}
        </Card>
        {settings.requestedTimeBudgetHours !== null ? (
          <Card tone="accent">
            <Text variant="label" style={styles.regular}>{t('settings.budget.asked', { hours: settings.requestedTimeBudgetHours })}</Text>
            <Button kind="secondary" title={t('plan.talkItThrough')} onPress={() => router.navigate('/coach')} style={styles.start} />
          </Card>
        ) : null}
      </Section>

      <Section title={t('settings.slots.title')}>
        <Card>
          <Text variant="label" style={[styles.regular, underBudget ? styles.warn : styles.mutedText]}>{t('settings.slots.summary', { slots: slotTotal, budget: total })}</Text>
          {settings.slots.map((s) => (
            <ListRow
              key={s.id}
              icon="time-outline"
              title={s.label}
              subtitle={[slotDays(s.days, t), `${timeLabel(minutesOf(s.start))}–${timeLabel(minutesOf(s.end))}`].join(' · ')
                + (s.pending ? ` · ${t('settings.slots.fromRefresh', { days: slotDays(s.pending.days, t), start: timeLabel(minutesOf(s.pending.start)), end: timeLabel(minutesOf(s.pending.end)) })}` : '')}
              onPress={() => setEditing({ slot: s })}
            />
          ))}
          {settings.newSlots.map((s) => (
            <ListRow
              key={s.id}
              icon="time-outline"
              tint={theme.colors.partly}
              title={s.label}
              subtitle={t('settings.slots.fromRefresh', { days: slotDays(s.days, t), start: timeLabel(minutesOf(s.start)), end: timeLabel(minutesOf(s.end)) })}
              onPress={() => setEditing({ slot: null, newSlot: s })}
            />
          ))}
          <Button kind="quiet" icon="add" title={t('settings.slots.add')} onPress={() => setEditing({ slot: null })} style={styles.start} />
          <Text variant="caption" muted>{t('settings.slots.applyNote')}</Text>
        </Card>
      </Section>

      <Section title={t('settings.coachSection')}>
        <Card>
          <Text variant="label">{t('settings.contact.title')}</Text>
          <Segmented
            options={FREQUENCIES.map((f) => ({ key: f, label: t(`settings.contact.${f}`) }))}
            value={settings.contactFrequency ?? 'Gentle'}
            onChange={(f) => void run(api.setContactFrequency(f))}
          />
          <Text variant="caption" muted>{t(`settings.contact.line.${settings.contactFrequency ?? 'Gentle'}`)}</Text>
          <ListRow
            icon="moon-outline"
            title={t('settings.quiet.title')}
            subtitle={t('settings.quiet.value', { start: settings.quietStart, end: settings.quietEnd, hours: quiet ? quietHours(quiet) : 0 })}
            onPress={() => setQuietOpen(true)}
            last
          />
        </Card>
      </Section>

      <Section title={t('settings.appearance.title')}>
        <Card>
          <Segmented options={APPEARANCES.map((a) => ({ key: a, label: t(`settings.appearance.${a}`) }))} value={appearance} onChange={setAppearance} />
        </Card>
      </Section>

      <Section title={t('settings.reminders.title')}>
        {exactAlarms === false ? (
          <Card style={styles.warnCard}>
            <View style={styles.headRow}>
              <Ionicons name="alarm-outline" size={18} color={theme.colors.warn} />
              <Text variant="label" style={[styles.flex, styles.warn]}>{t('settings.reminders.warning')}</Text>
            </View>
            <Text variant="label" muted style={styles.regular}>{t('settings.reminders.example')}</Text>
            <Button title={t('settings.reminders.allow')} onPress={() => void allowExactAlarms()} style={styles.start} />
          </Card>
        ) : (
          <Card>
            <ListRow icon="alarm-outline" tint={theme.colors.done} title={t('settings.reminders.allowed')} chevron={false} last />
          </Card>
        )}
      </Section>

      <Section title={t('settings.refresh.title')}>
        <Card>
          <View style={styles.week}>
            {REFRESH_DAYS.map((d) => {
              const on = settings.refreshDay === d;
              return (
                <Pressable key={d} accessibilityRole="button" accessibilityState={{ selected: on }} accessibilityLabel={t(`settings.dayLong.${d}`)}
                  onPress={() => void run(api.setWeeklyRefresh(d, settings.refreshTime))} style={[styles.dayCell, on && styles.dayCellOn]}>
                  <Text variant="label" style={on ? styles.onAccent : undefined}>{t(`settings.day.${d.slice(0, 3)}`)}</Text>
                </Pressable>
              );
            })}
          </View>
          <View style={styles.between}>
            <Text variant="heading">{settings.refreshTime}</Text>
            <View style={styles.steppers}>
              <Stepper icon="remove" label={t('settings.refresh.earlier')} onPress={() => void run(api.setWeeklyRefresh(settings.refreshDay, shiftTime(settings.refreshTime, -30)))} />
              <Stepper icon="add" label={t('settings.refresh.later')} onPress={() => void run(api.setWeeklyRefresh(settings.refreshDay, shiftTime(settings.refreshTime, 30)))} />
            </View>
          </View>
          <Text variant="caption" muted>{t('settings.refresh.value', { day: t(`settings.dayLong.${settings.refreshDay}`), time: settings.refreshTime })}</Text>
        </Card>
      </Section>

      <Section title={t('settings.moreSection')}>
        {pauseEverything ? (
          <Card style={styles.dashed}>
            <Text>{pauseEverything.until ? t('settings.pause.pausedUntil', { date: formatDay(pauseEverything.until, { locale: i18n.language }) }) : t('settings.pause.pausedOpen')}</Text>
            <Button title={t('pause.resumeNow')} onPress={() => void paused.resume(pauseEverything.pauseId)} style={styles.start} />
          </Card>
        ) : null}
        <Card>
          {pauseEverything ? null : <ListRow icon="pause-circle-outline" title={t('settings.pause.title')} subtitle={t('settings.pause.note')} onPress={() => setPauseOpen(true)} />}
          <ListRow icon="play-circle-outline" title={t('settings.replayTour')} onPress={() => void firstRun.replayTour().then(() => router.navigate('/'))} last />
        </Card>
      </Section>

      <Section title={t('settings.myData.title')}>
        <Card>
          <ListRow icon="download-outline" title={exporting ? t('settings.myData.exporting') : t('settings.myData.export')} subtitle={t('settings.myData.exportShort')} onPress={() => void exportData()} />
          <ListRow icon="trash-outline" tint={theme.colors.warn} title={t('settings.myData.delete')} onPress={() => setDeleteOpen(true)} last />
        </Card>
        <Text variant="caption" muted>{t('settings.myData.exportNote')}</Text>
        <Text variant="caption" muted>{t('settings.myData.privacyBackups')}</Text>
        <Text variant="caption" muted>{t('settings.myData.privacyProvider')}</Text>
      </Section>

      <Section title={t('settings.account')}>
        <Card>
          <ListRow icon="person-outline" title={t('settings.you.google')} chevron={false}
            end={<Button kind="quiet" title={t('settings.signOut')} onPress={() => void signOut()} style={styles.inline} />} last />
        </Card>
      </Section>

      {editing ? (
        <SlotSheet
          title={editing.slot?.label ?? editingNew?.label ?? t('settings.slots.newSlot')}
          initial={
            editing.slot
              ? { label: editing.slot.label, days: editing.slot.days, start: editing.slot.start, end: editing.slot.end }
              : editingNew
                ? { label: editingNew.label, days: editingNew.days, start: editingNew.start, end: editingNew.end }
                : { label: '', days: ['mon'], start: '07:00', end: '08:00' }
          }
          onSave={saveSlot}
          onRemove={editingNew ? () => {
            setEditing(null);
            void run(api.removeNewSlot(editingNew.id));
          } : undefined}
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
      {pauseOpen ? (
        <Sheet onClose={() => setPauseOpen(false)} label={t('common.close')}>
          <Text variant="heading">{t('settings.pause.title')}</Text>
          <Text variant="label" muted style={styles.regular}>{t('settings.pause.note')}</Text>
          {fewDays ? (
            <View style={styles.legend}>
              {FEW_DAYS.map((n) => (
                <Button key={n} kind="secondary" title={t('settings.pause.days', { count: n })} onPress={() => {
                  setPauseOpen(false);
                  void paused.start({}, { days: n });
                }} />
              ))}
            </View>
          ) : (
            <View>
              <ListRow icon="moon-outline" title={t('settings.pause.today')} chevron={false} onPress={() => { setPauseOpen(false); void paused.start({}, 'today'); }} />
              <ListRow icon="calendar-outline" title={t('settings.pause.fewDays')} onPress={() => setFewDays(true)} />
              <ListRow icon="pause-circle-outline" title={t('settings.pause.open')} chevron={false} onPress={() => { setPauseOpen(false); void paused.start({}, 'open'); }} last />
            </View>
          )}
        </Sheet>
      ) : null}
      {deleteOpen ? (
        <Sheet onClose={() => setDeleteOpen(false)} label={t('settings.myData.cancel')}>
          <Text variant="heading">{t('settings.myData.deleteTitle')}</Text>
          <Text>{t('settings.myData.deleteBody')}</Text>
          <Text variant="label" style={styles.regular}>{t('settings.myData.copyFirst')}</Text>
          <Button kind="secondary" icon="download-outline" title={t('settings.myData.exportFirst')} onPress={() => void exportData()} />
          {deleteFailed ? <Text variant="label" style={styles.warn}>{t('settings.myData.deleteFailed')}</Text> : null}
          <Button kind="danger" icon="trash-outline" title={t('settings.myData.deleteConfirm')} onPress={() => {
            setDeleteFailed(false);
            void session.deleteAccount(myData.deleteAccount).catch(() => setDeleteFailed(true));
          }} />
          <Button kind="quiet" title={t('settings.myData.cancel')} onPress={() => setDeleteOpen(false)} />
        </Sheet>
      ) : null}
    </ScrollView>
  );
}

function Stepper({ icon, label, onPress }: { icon: 'add' | 'remove'; label: string; onPress: () => void }) {
  const { theme } = useUnistyles();
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} hitSlop={6} style={({ pressed }) => [styles.stepper, pressed && styles.pressed]}>
      <Ionicons name={icon} size={18} color={theme.colors.accent} />
    </Pressable>
  );
}

function Segmented<K extends string>({ options, value, onChange }: { options: { key: K; label: string }[]; value: K; onChange: (k: K) => void }) {
  return (
    <View style={styles.segments} accessibilityRole="tablist">
      {options.map((o) => (
        <Pressable key={o.key} accessibilityRole="tab" accessibilityState={{ selected: value === o.key }} onPress={() => onChange(o.key)}
          style={[styles.segmentItem, value === o.key && styles.segmentOn]}>
          <Text variant="label" numberOfLines={1} style={value === o.key ? undefined : styles.mutedText}>{o.label}</Text>
        </Pressable>
      ))}
    </View>
  );
}

function slotDays(days: string[], t: (k: string) => string): string {
  return days.map((d) => t(`settings.day.${d}`)).join(' ');
}

function shiftTime(time: string, by: number): string {
  const total = (minutesOf(time) + by + 1440) % 1440;
  return timeLabel(total);
}

function BarSegment({ hue, fraction }: { hue: Hue; fraction: number }) {
  return <View style={[styles.segment(hue), { width: `${fraction * 100}%` }]} />;
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { padding: theme.space.lg, paddingBottom: theme.space.xl + rt.insets.bottom, gap: 20 },
  between: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: theme.space.md },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm },
  flex: { flex: 1 },
  start: { alignSelf: 'flex-start' },
  inline: { minHeight: 32, paddingHorizontal: theme.space.sm, paddingVertical: 2 },
  regular: { fontWeight: '400' },
  warn: { color: theme.colors.warn },
  mutedText: { color: theme.colors.textMuted },
  onAccent: { color: theme.colors.onAccent },
  pressed: { opacity: 0.7 },
  dashed: { borderStyle: 'dashed' },
  warnCard: { borderColor: theme.colors.warn },
  steppers: { flexDirection: 'row', gap: theme.space.sm },
  stepper: { width: 32, height: 32, borderRadius: 16, alignItems: 'center', justifyContent: 'center', backgroundColor: theme.colors.accentTint },
  bar: { flexDirection: 'row', height: 10, borderRadius: 5, overflow: 'hidden', backgroundColor: theme.colors.surfaceAlt },
  segment: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  legend: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.md },
  legendItem: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  dot: { width: 8, height: 8, borderRadius: 4 },
  week: { flexDirection: 'row', gap: theme.space.xs },
  dayCell: { flex: 1, alignItems: 'center', paddingVertical: 10, borderRadius: theme.radii.sm, borderWidth: 1, borderColor: theme.colors.border },
  dayCellOn: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  segments: { flexDirection: 'row', padding: 3, borderRadius: theme.radii.pill, backgroundColor: theme.colors.surfaceAlt },
  segmentItem: { flex: 1, alignItems: 'center', paddingVertical: theme.space.sm, borderRadius: theme.radii.pill },
  segmentOn: { backgroundColor: theme.colors.surface },
}));
