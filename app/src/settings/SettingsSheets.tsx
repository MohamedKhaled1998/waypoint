import { useState } from 'react';
import { Modal, Pressable, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { Button } from '../ui/Button';
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

function Sheet({ children, onClose }: { children: React.ReactNode; onClose: () => void }) {
  return (
    <Modal transparent animationType="slide" onRequestClose={onClose}>
      <Pressable style={styles.scrim} onPress={onClose} accessibilityRole="button" />
      <View style={styles.sheet}>{children}</View>
    </Modal>
  );
}

function Chip({ label, selected, onPress, accessibilityLabel }: { label: string; selected: boolean; onPress: () => void; accessibilityLabel?: string }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel ?? label} accessibilityState={{ selected }} onPress={onPress} style={[styles.chip, selected && styles.chipOn]}>
      <Text variant="label">{label}</Text>
    </Pressable>
  );
}
export { Chip };

export type SlotDraft = { label: string; days: string[]; start: string; end: string };

/** One Slot: day toggles and a two-handle track from 05:00 to 24:00, 15-minute snap. */
export function SlotSheet({ initial, onSave, onClose }: { initial: SlotDraft; onSave: (d: SlotDraft) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const [days, setDays] = useState(initial.days);
  const [w, setW] = useState<Span>(() => slotWindow(initial.start, initial.end));
  return (
    <Sheet onClose={onClose}>
      <Text variant="heading">{t('settings.slots.sheetTitle')}</Text>
      <View style={styles.row}>
        {WEEK.map((d) => (
          <Chip
            key={d}
            label={t(`settings.day.${d}`)}
            selected={days.includes(d)}
            onPress={() => setDays((cur) => (cur.includes(d) ? cur.filter((x) => x !== d) : [...cur, d]))}
          />
        ))}
      </View>
      <View style={styles.between}>
        <Text>{t('settings.slots.start')}: {timeLabel(w.start)}</Text>
        <Text>{t('settings.slots.end')}: {timeLabel(w.end)}</Text>
      </View>
      <TwoHandleTrack
        from={SLOT_FROM}
        to={SLOT_TO}
        value={w}
        onMove={(handle, minutes) => setW((cur) => moveSlotHandle(cur, handle, minutes))}
        startLabel={t('settings.slots.start')}
        endLabel={t('settings.slots.end')}
      />
      <View style={styles.row}>
        <Button kind="quiet" title={t('settings.slots.cancel')} onPress={onClose} />
        <Button
          title={t('settings.slots.save')}
          disabled={days.length === 0}
          onPress={() => onSave({ label: initial.label, days: WEEK.filter((d) => days.includes(d)), ...slotWindowToText(w) })}
        />
      </View>
    </Sheet>
  );
}

/** Quiet hours: one span on a noon-to-noon track, so an overnight span is one piece; with presets. */
export function QuietSheet({ start, end, onSave, onClose }: { start: string; end: string; onSave: (start: string, end: string) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const [track, setTrack] = useState<Span>(() => quietToTrack(start, end) ?? { start: 600, end: 1140 });
  const { start: from, end: to } = quietFromTrack(track);
  return (
    <Sheet onClose={onClose}>
      <Text variant="heading">{t('settings.quiet.sheetTitle')}</Text>
      <Text variant="heading">{t('settings.quiet.total', { hours: quietHours(track) })}</Text>
      <View style={styles.between}>
        <Text>{t('settings.quiet.from')}: {from}</Text>
        <Text>{t('settings.quiet.until')}: {to}</Text>
      </View>
      <TwoHandleTrack
        from={0}
        to={DAY}
        value={track}
        onMove={(handle, minutes) => setTrack((cur) => moveQuietHandle(cur, handle, minutes))}
        startLabel={t('settings.quiet.from')}
        endLabel={t('settings.quiet.until')}
      />
      <View style={styles.between}>
        <Text variant="caption" muted>12:00</Text>
        <Text variant="caption" muted>00:00</Text>
        <Text variant="caption" muted>12:00</Text>
      </View>
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
      <View style={styles.row}>
        <Button kind="quiet" title={t('settings.quiet.cancel')} onPress={onClose} />
        <Button title={t('settings.quiet.save')} onPress={() => onSave(from, to)} />
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  scrim: { flex: 1, backgroundColor: theme.colors.scrim },
  sheet: {
    backgroundColor: theme.colors.surface,
    borderTopStartRadius: theme.radii.lg,
    borderTopEndRadius: theme.radii.lg,
    padding: theme.space.lg,
    paddingBottom: theme.space.lg + rt.insets.bottom,
    gap: theme.space.lg,
  },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, alignItems: 'center' },
  between: { flexDirection: 'row', justifyContent: 'space-between' },
  chip: {
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  chipOn: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
}));
