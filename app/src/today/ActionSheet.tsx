import { useMemo, useState } from 'react';
import { Modal, Pressable, ScrollView, TextInput, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Button } from '../ui/Button';
import { Text } from '../ui/Text';
import { FEELING_CHIPS, SKIP_CHIPS, STOPPED_SHORT_CHIPS, createActionSheet, partlyChips } from './sheet';
import type { CheckInRequest, CheckInState, ScheduledActionView } from './today';

type Props = {
  action: ScheduledActionView | null;
  initialChoice?: CheckInState;
  onClose: () => void;
  onSubmit: (req: CheckInRequest) => Promise<void>;
};

function Chip({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ selected }} onPress={onPress} style={[styles.chip, selected && styles.chipOn]}>
      <Text variant="label">{label}</Text>
    </Pressable>
  );
}

/** The bottom sheet for one Action: step 1 the result tiles, step 2 one optional follow-up. */
export function ActionSheet({ action, initialChoice, onClose, onSubmit }: Props) {
  if (!action) return null;
  return <Sheet key={action.id + (initialChoice ?? '')} action={action} initialChoice={initialChoice} onClose={onClose} onSubmit={onSubmit} />;
}

function Sheet({ action, initialChoice, onClose, onSubmit }: Props & { action: ScheduledActionView }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const sheet = useMemo(() => {
    const s = createActionSheet({ scheduledActionId: action.id, minutes: action.minutes });
    if (initialChoice) s.choose(initialChoice);
    return s;
  }, [action.id, action.minutes, initialChoice]);
  const [, setTick] = useState(0);
  const update = (fn: () => void) => {
    fn();
    setTick((n) => n + 1);
  };
  const [notToday, setNotToday] = useState(false);
  const [failed, setFailed] = useState(false);
  const s = sheet.state();
  const request = sheet.request(`ci-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`, new Date().toISOString());

  const send = async (req: CheckInRequest) => {
    try {
      await onSubmit(req);
      onClose();
    } catch {
      setFailed(true);
    }
  };
  const chips = s.followUp === 'feeling' ? FEELING_CHIPS : s.followUp === 'stoppedShort' ? STOPPED_SHORT_CHIPS : SKIP_CHIPS;

  return (
    <Modal transparent animationType="slide" visible onRequestClose={onClose} statusBarTranslucent navigationBarTranslucent>
      <Pressable style={styles.scrim} onPress={onClose} accessibilityLabel={t('today.sheet.close')} />
      <View style={styles.sheet}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <Text variant="heading">{action.title}</Text>
          <Text muted>{t('today.sheet.meta', { time: action.slotStart ?? '', minutes: action.minutes })}</Text>

          {s.step === 1 && !notToday ? (
            <>
              <View style={styles.tiles}>
                {(['done', 'partly', 'skipped'] as const).map((choice) => (
                  <Pressable key={choice} accessibilityRole="button" style={styles.tile} onPress={() => update(() => sheet.choose(choice))}>
                    <Text variant="heading">{t(`today.state.${choice}`)}</Text>
                  </Pressable>
                ))}
              </View>
              <Button kind="quiet" title={t('today.sheet.notToday')} onPress={() => setNotToday(true)} />
            </>
          ) : null}

          {s.step === 1 && notToday ? (
            <>
              <Text variant="label">{t('today.sheet.notTodayTitle')}</Text>
              {sheet.notTodayScopes.map((scope) => (
                <Button
                  key={scope}
                  kind="secondary"
                  title={t(`today.sheet.scope.${scope}`)}
                  onPress={() => void send({ ...sheetSkip(action.id) })}
                />
              ))}
              <Button kind="quiet" title={t('today.sheet.back')} onPress={() => setNotToday(false)} />
            </>
          ) : null}

          {s.step === 2 ? (
            <>
              <Text variant="label">{t(`today.sheet.prompt.${s.followUp}`)}</Text>
              {s.followUp === 'stoppedShort' ? (
                <>
                  <Text variant="label" muted>
                    {t('today.sheet.howMuch')}
                  </Text>
                  <View style={styles.chips}>
                    {partlyChips(action.minutes).map((m) => (
                      <Chip key={m} label={t('today.sheet.minutes', { count: m })} selected={s.minutes === m} onPress={() => update(() => sheet.pickMinutes(m))} />
                    ))}
                    <Chip label={t('today.sheet.other')} selected={s.minutes === null && s.otherMinutes !== ''} onPress={() => update(() => sheet.setOtherMinutes('0'))} />
                  </View>
                  {s.minutes === null && s.otherMinutes !== '' ? (
                    <TextInput
                      style={styles.input}
                      keyboardType="number-pad"
                      value={s.otherMinutes === '0' ? '' : s.otherMinutes}
                      placeholder={t('today.sheet.otherPlaceholder')}
                      placeholderTextColor={theme.colors.textMuted}
                      onChangeText={(text) => update(() => sheet.setOtherMinutes(text || '0'))}
                    />
                  ) : null}
                </>
              ) : null}
              <View style={styles.chips}>
                {chips.map((code) => (
                  <Chip key={code} label={t(`today.reason.${code}`)} selected={s.reasons.includes(code)} onPress={() => update(() => sheet.toggleReason(code))} />
                ))}
              </View>
              {s.showFreeText ? (
                <TextInput
                  style={styles.input}
                  multiline
                  value={s.note}
                  placeholder={t('today.sheet.notePlaceholder')}
                  placeholderTextColor={theme.colors.textMuted}
                  onChangeText={(text) => update(() => sheet.setNote(text))}
                />
              ) : null}
              {s.followUp === 'skipReason' ? <Text muted>{t('today.sheet.noNeedToExplain')}</Text> : null}
              {s.coachWillAsk ? <Text style={styles.care}>{t('today.sheet.coachWillAsk')}</Text> : null}
              {failed ? <Text style={styles.care}>{t('today.sheet.failed')}</Text> : null}
              <Button title={t('today.sheet.save')} disabled={!request} onPress={() => request && void send(request)} />
              <Button kind="quiet" title={t('today.sheet.back')} onPress={() => update(() => sheet.back())} />
            </>
          ) : null}
        </ScrollView>
      </View>
    </Modal>
  );
}

function sheetSkip(id: string): CheckInRequest {
  return {
    scheduledActionId: id,
    clientId: `ci-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
    state: 'skipped',
    minutes: null,
    reasons: ['not_today'],
    note: null,
    tappedAt: new Date().toISOString(),
  };
}

const styles = StyleSheet.create((theme) => ({
  scrim: { flex: 1, backgroundColor: 'rgba(0,0,0,0.4)' },
  sheet: {
    backgroundColor: theme.colors.surface,
    borderTopStartRadius: theme.radii.lg,
    borderTopEndRadius: theme.radii.lg,
    maxHeight: '85%',
  },
  content: { padding: theme.space.xl, gap: theme.space.md },
  tiles: { flexDirection: 'row', gap: theme.space.sm },
  tile: {
    flex: 1,
    minHeight: 88,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.accentTint,
  },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: {
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  chipOn: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
  input: {
    minHeight: 44,
    borderWidth: 1,
    borderColor: theme.colors.border,
    borderRadius: theme.radii.sm,
    padding: theme.space.md,
    color: theme.colors.text,
  },
  care: { color: theme.colors.danger },
}));
