import Ionicons from '@expo/vector-icons/Ionicons';
import { useMemo, useState } from 'react';
import { Pressable, ScrollView, TextInput, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { formatDay } from '../i18n/dates';
import type { Hue } from '../theme/tokens';
import { Button } from '../ui/Button';
import { Sheet } from '../ui/Sheet';
import { Text } from '../ui/Text';
import { guideSheet } from './guide';
import type { SlotView } from './offline';
import { FEELING_CHIPS, SKIP_CHIPS, STOPPED_SHORT_CHIPS, createActionSheet, moveOptions, partlyChips } from './sheet';
import type { CheckInRequest, CheckInState, ScheduledActionView } from './today';
import type { PauseScope, PauseUntil } from '../pause/pause';

const FEW_DAYS = [2, 3, 5, 7] as const;

type Props = {
  action: ScheduledActionView | null;
  initialChoice?: CheckInState;
  onClose: () => void;
  onSubmit: (req: CheckInRequest) => Promise<void>;
  slots: SlotView[];
  onMove: (date: string, slotId: string) => Promise<void>;
  /** The cached Actions, to count how many times this one was done. */
  history: ScheduledActionView[];
  now: { date: string; time: string };
  /** "Not today…": starts a Pause at once, with no approval. */
  onPause: (scope: PauseScope, until: PauseUntil) => Promise<void>;
  /** For the header: the Action's Aspiration by name and hue. */
  aspirations: { aspirationId: string; shortName: string; hue: string }[];
};

const TILES = [
  ['done', 'checkmark-circle'],
  ['partly', 'contrast'],
  ['skipped', 'play-skip-forward-circle'],
] as const;

function Chip({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ selected }} onPress={onPress} style={[styles.chip, selected && styles.chipOn]}>
      <Text variant="label" style={selected ? styles.accentText : undefined}>{label}</Text>
    </Pressable>
  );
}

/** The bottom sheet for one Action: step 1 the result tiles, step 2 one optional follow-up. */
export function ActionSheet({ action, ...rest }: Props) {
  if (!action) return null;
  return <ActionSheetBody key={action.id + (rest.initialChoice ?? '')} action={action} {...rest} />;
}

function ActionSheetBody({ action, initialChoice, onClose, onSubmit, slots, onMove, now, onPause, history, aspirations }: Props & { action: ScheduledActionView }) {
  const { t, i18n } = useTranslation();
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
  const how = useMemo(() => guideSheet(action, history), [action, history]);
  const [howOpen, setHowOpen] = useState(how?.openByItself ?? false);
  const [notToday, setNotToday] = useState(false);
  const [fewDays, setFewDays] = useState(false);
  const [moving, setMoving] = useState(false);
  const options = useMemo(() => moveOptions(action, slots, now), [action, slots, now]);
  const [failed, setFailed] = useState(false);
  const s = sheet.state();
  const request = sheet.request(`ci-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`, new Date().toISOString());

  const aspiration = aspirations.find((a) => a.aspirationId === action.aspirationId);
  const hue = (['coral', 'teal', 'amber'] as const).find((h) => h === aspiration?.hue) as Hue | undefined;
  const hueColor = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
  const stateColor = { done: theme.colors.done, partly: theme.colors.partly, skipped: theme.colors.skipped };
  const otherOn = s.minutes === null && s.otherMinutes !== '';

  const send = async (req: CheckInRequest) => {
    try {
      await onSubmit(req);
      onClose();
    } catch {
      setFailed(true);
    }
  };
  const chips = s.followUp === 'feeling' ? FEELING_CHIPS : s.followUp === 'stoppedShort' ? STOPPED_SHORT_CHIPS : SKIP_CHIPS;
  const back = (onPress: () => void) => <Button kind="quiet" icon="chevron-back" title={t('today.sheet.back')} onPress={onPress} style={styles.back} />;

  const meta = action.anchor
    ? t('today.sheet.metaAnchor', { anchor: action.anchor.words, time: action.slotStart ?? action.anchor.time })
    : [
        t('today.sheet.metaSlot', { start: action.slotStart ?? '', end: action.slotEnd ?? '', minutes: action.minutes }),
        action.kind === 'task' ? t('today.sheet.oneOff') : null,
      ].filter(Boolean).join(' · ');

  return (
    <Sheet onClose={onClose} label={t('today.sheet.close')}>
      <View style={styles.header}>
        {aspiration ? (
          <View style={styles.headRow}>
            <View style={[styles.dot, { backgroundColor: hueColor }]} />
            <Text variant="caption" style={{ color: hueColor }}>{aspiration.shortName}</Text>
          </View>
        ) : null}
        <Text variant="heading">{action.title}</Text>
        <Text variant="label" muted style={styles.regular}>
          {[meta, action.anchor && action.targetMinutes ? t('today.target', { count: action.targetMinutes }) : null].filter(Boolean).join(' · ')}
        </Text>
        {action.checkIn ? (
          <Text variant="label" style={{ color: stateColor[action.checkIn.state] }}>
            {t('today.sheet.checkedIn', {
              result: action.checkIn.state === 'partly' ? t('today.result.partly', { minutes: action.checkIn.minutes ?? 0, planned: action.minutes }) : t(`today.state.${action.checkIn.state}`),
            })}
          </Text>
        ) : null}
      </View>

      <ScrollView style={styles.body} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {s.step === 1 && !notToday && !moving ? (
          <>
            <View style={styles.tiles}>
              {TILES.map(([choice, icon]) => (
                <Pressable
                  key={choice}
                  accessibilityRole="button"
                  onPress={() => update(() => sheet.choose(choice))}
                  style={({ pressed }) => [styles.tile, { backgroundColor: `${stateColor[choice]}18`, borderColor: `${stateColor[choice]}44` }, pressed && styles.pressed]}
                >
                  <Ionicons name={icon} size={30} color={stateColor[choice]} />
                  <Text style={[styles.bold, { color: stateColor[choice] }]}>{t(`today.state.${choice}`)}</Text>
                </Pressable>
              ))}
            </View>
            <Button kind="secondary" title={t('today.sheet.notToday')} onPress={() => setNotToday(true)} />
            {!action.checkIn ? <Button kind="quiet" title={t('today.sheet.move')} onPress={() => setMoving(true)} /> : null}
            {how ? (
              <View style={styles.howTo}>
                <Pressable accessibilityRole="button" accessibilityState={{ expanded: howOpen }} onPress={() => setHowOpen(!howOpen)} style={styles.headRow}>
                  <Ionicons name="book-outline" size={14} color={theme.colors.textMuted} />
                  <Text variant="label" style={styles.flex}>{t(how.openByItself ? 'today.sheet.howToNew' : 'today.sheet.howTo')}</Text>
                  <Ionicons name={howOpen ? 'chevron-up' : 'chevron-down'} size={14} color={theme.colors.textMuted} />
                </Pressable>
                {howOpen ? (
                  <>
                    <Text variant="label" style={styles.regular}>{how.guide.body}</Text>
                    {how.guide.targets.map((g) => (
                      <Text key={g.what} variant="label" muted style={styles.regular}>
                        {t('today.sheet.howToTarget', { what: g.what, low: g.low, high: g.high, unit: g.unit })} · {g.why}
                      </Text>
                    ))}
                    {how.referralCard ? (
                      <View style={styles.referral}>
                        <Ionicons name="medkit-outline" size={16} color={theme.colors.warn} />
                        <View style={styles.flex}>
                          <Text variant="label" style={styles.warnText}>{t('today.sheet.referralTitle')}</Text>
                          <Text variant="label" style={styles.regular}>{how.referralCard}</Text>
                        </View>
                      </View>
                    ) : null}
                  </>
                ) : null}
              </View>
            ) : null}
          </>
        ) : null}

        {s.step === 1 && moving ? (
          <>
            {back(() => setMoving(false))}
            <Text style={styles.bold}>{t('today.sheet.moveTitle')}</Text>
            {options.length === 0 ? <Text muted>{t('today.sheet.moveNone')}</Text> : null}
            {options.map((o) => (
              <Button
                key={`${o.date}-${o.slotId}`}
                kind="secondary"
                title={t('today.sheet.moveOption', { day: formatDay(o.date, { locale: i18n.language }), time: o.start, label: o.label })}
                onPress={() => void onMove(o.date, o.slotId).then(onClose, () => setFailed(true))}
              />
            ))}
            {failed ? <Text variant="label" style={styles.warnText}>{t('today.sheet.failed')}</Text> : null}
          </>
        ) : null}

        {s.step === 1 && notToday ? (
          <>
            {back(() => (fewDays ? setFewDays(false) : setNotToday(false)))}
            <Text style={styles.bold}>{t(fewDays ? 'today.sheet.fewDaysTitle' : 'today.sheet.notTodayTitle')}</Text>
            {!fewDays ? (
              <>
                <Button title={t('today.sheet.scope.thisAction')} onPress={() => void onPause({ actionId: action.id }, 'today').then(onClose, () => setFailed(true))} />
                <Button kind="secondary" title={t('today.sheet.scope.everythingToday')} onPress={() => void onPause({}, 'today').then(onClose, () => setFailed(true))} />
                <Button kind="quiet" title={t('today.sheet.scope.fewDays')} onPress={() => setFewDays(true)} />
              </>
            ) : (
              FEW_DAYS.map((days) => (
                <Button key={days} kind="secondary" title={t('pause.days', { count: days })} onPress={() => void onPause({}, { days }).then(onClose, () => setFailed(true))} />
              ))
            )}
            {failed ? <Text variant="label" style={styles.warnText}>{t('today.sheet.failed')}</Text> : null}
          </>
        ) : null}

        {s.step === 2 && s.choice ? (
          <>
            {back(() => update(() => sheet.back()))}
            {s.followUp === 'stoppedShort' ? (
              <View style={styles.group}>
                <Text style={styles.bold}>{t('today.sheet.howMuch')}</Text>
                <View style={styles.chips}>
                  {partlyChips(action.minutes).map((m) => (
                    <Chip key={m} label={t('today.sheet.minutes', { count: m })} selected={s.minutes === m} onPress={() => update(() => sheet.pickMinutes(m))} />
                  ))}
                  <Chip label={t('today.sheet.other')} selected={otherOn} onPress={() => update(() => sheet.setOtherMinutes('0'))} />
                </View>
                {otherOn ? (
                  <View style={styles.headRow}>
                    <TextInput
                      autoFocus
                      style={styles.minutesInput}
                      keyboardType="number-pad"
                      value={s.otherMinutes === '0' ? '' : s.otherMinutes}
                      placeholder={t('today.sheet.otherPlaceholder')}
                      placeholderTextColor={theme.colors.textMuted}
                      onChangeText={(text) => update(() => sheet.setOtherMinutes(text.replace(/[^0-9]/g, '') || '0'))}
                    />
                    <Text variant="label" muted style={styles.regular}>{t('today.sheet.ofMinutes', { count: action.minutes })}</Text>
                  </View>
                ) : null}
              </View>
            ) : null}
            <View style={styles.group}>
              <Text style={s.followUp === 'stoppedShort' ? undefined : styles.bold}>{t(`today.sheet.prompt.${s.followUp}`)}</Text>
              <View style={styles.chips}>
                {chips.map((code) => (
                  <Chip key={code} label={t(`today.reason.${code}`)} selected={s.reasons.includes(code)} onPress={() => update(() => sheet.toggleReason(code))} />
                ))}
              </View>
              {s.showFreeText ? (
                <TextInput
                  autoFocus
                  style={styles.note}
                  multiline
                  value={s.note}
                  placeholder={t('today.sheet.notePlaceholder')}
                  placeholderTextColor={theme.colors.textMuted}
                  onChangeText={(text) => update(() => sheet.setNote(text))}
                />
              ) : null}
            </View>
            {failed ? <Text variant="label" style={styles.warnText}>{t('today.sheet.failed')}</Text> : null}
            <Button
              title={request ? t('today.sheet.submit', { state: t(`today.state.${s.choice}`) }) : t('today.sheet.pickMinutes')}
              disabled={!request}
              onPress={() => request && void send(request)}
            />
            {s.coachWillAsk ? <Text variant="label" muted style={styles.regular}>{t('today.sheet.coachWillAsk')}</Text> : null}
          </>
        ) : null}
      </ScrollView>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  header: { gap: theme.space.xs },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  dot: { width: 8, height: 8, borderRadius: 4 },
  body: { flexGrow: 0 },
  content: { gap: theme.space.lg, paddingBottom: theme.space.sm },
  flex: { flex: 1 },
  bold: { fontWeight: '700' },
  regular: { fontWeight: '400' },
  pressed: { opacity: 0.7 },
  accentText: { color: theme.colors.accent },
  warnText: { color: theme.colors.warn },
  back: { alignSelf: 'flex-start', paddingHorizontal: 0, minHeight: 32, paddingVertical: 2 },
  group: { gap: theme.space.sm },
  tiles: { flexDirection: 'row', gap: theme.space.sm },
  tile: { flex: 1, alignItems: 'center', gap: 6, paddingVertical: 18, borderRadius: theme.radii.lg, borderWidth: 1 },
  howTo: { gap: 6, padding: theme.space.md, borderRadius: theme.radii.md, backgroundColor: theme.colors.surfaceAlt },
  referral: {
    flexDirection: 'row',
    gap: theme.space.sm,
    padding: 10,
    borderRadius: theme.radii.sm,
    borderWidth: 1,
    borderColor: `${theme.colors.warn}55`,
    backgroundColor: `${theme.colors.warn}10`,
  },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: { paddingHorizontal: 14, paddingVertical: theme.space.sm, borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border },
  chipOn: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
  minutesInput: {
    width: 72,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.surfaceAlt,
    color: theme.colors.text,
    textAlign: 'center',
    fontSize: 16,
  },
  note: {
    minHeight: 44,
    paddingHorizontal: theme.space.md,
    paddingVertical: 10,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.surfaceAlt,
    color: theme.colors.text,
    textAlignVertical: 'top',
    ...theme.type.body,
  },
}));
