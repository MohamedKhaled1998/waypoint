import { useState } from 'react';
import { Pressable, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { Button } from '../ui/Button';
import { Sheet } from '../ui/Sheet';
import { Text } from '../ui/Text';
import { TwoHandleTrack } from '../ui/TwoHandleTrack';
import {
  DAY,
  QUIET_PRESETS,
  SLOT_FROM,
  SLOT_TO,
  moveQuietHandle,
  moveSlotHandle,
  quietFromTrack,
  quietHours,
  quietToTrack,
  slotWindow,
  slotWindowToText,
  timeLabel,
  type Span,
} from './tracks';

export const WEEK = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;

function Chip({ label, selected, onPress, accessibilityLabel }: { label: string; selected: boolean; onPress: () => void; accessibilityLabel?: string }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel ?? label} accessibilityState={{ selected }} onPress={onPress} style={[styles.chip, selected && styles.chipOn]}>
      <Text variant="label">{label}</Text>
    </Pressable>
  );
}
export { Chip };

export type SlotDraft = { label: string; days: string[]; start: string; end: string };

/** One Slot: seven day cells and a two-handle track from 05:00 to 24:00, 15-minute snap. */
export function SlotSheet({ initial, title, onSave, onClose, onRemove }: {
  initial: SlotDraft; title: string; onSave: (d: SlotDraft) => void; onClose: () => void; onRemove?: () => void;
}) {
  const { t } = useTranslation();
  const [days, setDays] = useState(initial.days);
  const [w, setW] = useState<Span>(() => slotWindow(initial.start, initial.end));
  return (
    <Sheet onClose={onClose} label={t('settings.slots.cancel')}>
      <Text variant="heading">{title}</Text>
      <View style={styles.week}>
        {WEEK.map((d) => {
          const on = days.includes(d);
          return (
            <Pressable key={d} accessibilityRole="button" accessibilityState={{ selected: on }} accessibilityLabel={t(`settings.day.${d}`)}
              onPress={() => setDays((cur) => (cur.includes(d) ? cur.filter((x) => x !== d) : [...cur, d]))}
              style={[styles.dayCell, on && styles.dayCellOn]}>
              <Text variant="label" style={on ? styles.onAccent : undefined}>{t(`settings.day.${d}`)}</Text>
            </Pressable>
          );
        })}
      </View>
      <Text variant="title" style={styles.centered}>{t('settings.slots.range', { start: timeLabel(w.start), end: timeLabel(w.end) })}</Text>
      <TwoHandleTrack
        from={SLOT_FROM}
        to={SLOT_TO}
        value={w}
        onMove={(handle, minutes) => setW((cur) => moveSlotHandle(cur, handle, minutes))}
        startLabel={t('settings.slots.start')}
        endLabel={t('settings.slots.end')}
      />
      <Ticks labels={['05:00', '10:00', '15:00', '20:00', '24:00']} />
      <Text variant="caption" muted>{t('settings.slots.stayPut')}</Text>
      <View style={styles.actions}>
        {onRemove ? <Button kind="danger" title={t('settings.slots.remove')} onPress={onRemove} /> : <View />}
        <Button
          title={t('settings.slots.save')}
          disabled={days.length === 0}
          onPress={() => onSave({ label: initial.label, days: WEEK.filter((d) => days.includes(d)), ...slotWindowToText(w) })}
        />
      </View>
    </Sheet>
  );
}

function Ticks({ labels }: { labels: string[] }) {
  return (
    <View style={styles.between}>
      {labels.map((l) => (
        <Text key={l} variant="caption" muted>{l}</Text>
      ))}
    </View>
  );
}

/** Quiet hours: one span on a noon-to-noon track, so an overnight span is one piece; with presets. */
export function QuietSheet({ start, end, onSave, onClose }: { start: string; end: string; onSave: (start: string, end: string) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const [track, setTrack] = useState<Span>(() => quietToTrack(start, end) ?? { start: 600, end: 1140 });
  const { start: from, end: to } = quietFromTrack(track);
  return (
    <Sheet onClose={onClose} label={t('settings.quiet.cancel')}>
      <Text variant="heading">{t('settings.quiet.sheetTitle')}</Text>
      <Text variant="title" style={styles.centered}>{t('settings.slots.range', { start: from, end: to })}</Text>
      <Text variant="label" muted style={[styles.centered, styles.regular]}>{t('settings.quiet.total', { hours: quietHours(track) })}</Text>
      <TwoHandleTrack
        from={0}
        to={DAY}
        value={track}
        onMove={(handle, minutes) => setTrack((cur) => moveQuietHandle(cur, handle, minutes))}
        startLabel={t('settings.quiet.from')}
        endLabel={t('settings.quiet.until')}
      />
      <Ticks labels={['12:00', '18:00', '00:00', '06:00', '12:00']} />
      <View style={styles.row}>
        {QUIET_PRESETS.map((p) => (
          <Chip
            key={p.start + p.end}
            label={t('settings.quiet.preset', { start: p.start.slice(0, 2), end: p.end.slice(0, 2) })}
            selected={from === p.start && to === p.end}
            onPress={() => setTrack(quietToTrack(p.start, p.end)!)}
          />
        ))}
      </View>
      <Text variant="caption" muted>{t('settings.quiet.note')}</Text>
      <View style={styles.actions}>
        <Button kind="quiet" title={t('settings.quiet.cancel')} onPress={onClose} />
        <Button title={t('settings.quiet.save')} onPress={() => onSave(from, to)} />
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, alignItems: 'center' },
  between: { flexDirection: 'row', justifyContent: 'space-between' },
  actions: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: theme.space.sm },
  centered: { textAlign: 'center' },
  regular: { fontWeight: '400' },
  onAccent: { color: theme.colors.onAccent },
  week: { flexDirection: 'row', gap: theme.space.xs },
  dayCell: { flex: 1, alignItems: 'center', paddingVertical: 10, borderRadius: theme.radii.sm, borderWidth: 1, borderColor: theme.colors.border },
  dayCellOn: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  chip: {
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  chipOn: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
}));
