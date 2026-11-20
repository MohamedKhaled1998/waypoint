import Ionicons from '@expo/vector-icons/Ionicons';
import { useEffect, useRef, useState } from 'react';
import { Pressable, ScrollView, TextInput, View } from 'react-native';
import { KeyboardPadded } from '../ui/KeyboardPadded';
import { Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { interviewResume } from '../firstrun/firstRun';
import { firstRun } from '../firstrun/native';
import { useFirstRun } from '../firstrun/native';
import { StepCounter } from '../ui/StepCounter';
import { AnswerChip, Bubble, ChatList, CoachText, Composer, TypingDots, UserText, type ChatListHandle } from '../ui/chat/Chat';
import { formatDay } from '../i18n/dates';
import { SHORT_NAME_MAX, type Heard } from '../interview/interview';
import { useInterview } from '../interview/useInterview';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import { settleForLine } from '../aspiration/settleFor';

export default function InterviewScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const iv = useInterview();
  const { step } = useFirstRun();
  const inFirstRun = step === 'interview';
  // First run: the words came from the Aspiration step, so the Interview opens straight into the Coach's first question.
  const { words } = useLocalSearchParams<{ words?: string }>();
  const begun = useRef(false);
  useEffect(() => {
    if (!words || !iv.opened || iv.started || begun.current) return;
    begun.current = true;
    void iv.begin(String(words));
  }, [words, iv]);
  // Reopened at this step before any Interview was made: the Aspiration step is the one left, never the words box here.
  const goBack = interviewResume({ inFirstRun, opened: iv.opened, started: iv.started, hasWords: !!words }) === 'aspiration';
  useEffect(() => {
    if (goBack) void firstRun.advance('aspiration').then(() => router.replace('/aspiration'));
  }, [goBack, router]);
  function leave() {
    router.back();
  }
  // Confirming the picture hands the Plan to the Coach: first run waits on the drafting screen, otherwise the
  // thread, where the picture now sits as a card.
  const interviewDone = iv.step === 'done';
  useEffect(() => {
    if (!interviewDone) return;
    if (inFirstRun) void firstRun.advance('drafting').then(() => router.replace('/drafting'));
    else router.replace('/coach');
  }, [interviewDone, inFirstRun, router]);

  const insets = useSafeAreaInsets();
  const inChat = iv.started && (iv.step === 'chat' || iv.step === 'shortName' || iv.step === 'picture');
  return (
    // Reaches the bottom edge: clears the nav bar, or the keyboard when it is up.
    <KeyboardPadded minPadding={insets.bottom} style={styles.container}>
      <View style={styles.flex}>
        <Stack.Screen
          options={{
            title: t('interview.title'),
            headerBackVisible: false,
            gestureEnabled: false,
            headerRight: () => (
              <Pressable accessibilityRole="button" onPress={leave} hitSlop={8}>
                <Text variant="label" style={styles.leave}>{t('interview.saveAndLeave')}</Text>
              </Pressable>
            ),
          }}
        />
        {inFirstRun ? <View style={styles.counter}><StepCounter step="interview" /></View> : null}
        {!iv.started && !words ? <OwnWords onBegin={(words) => void iv.begin(words)} /> : null}
        {inChat ? (
          <View style={styles.top}>
            <ProgressLine answered={iv.messages.filter((m) => m.role === 'user').length} />
            <HeardStrip heard={iv.heard} />
          </View>
        ) : null}
        {inChat ? <Chat iv={iv} /> : null}
      </View>
    </KeyboardPadded>
  );
}

/** About 12 answers make an Interview; the line fills as they come in. */
const EXPECTED_ANSWERS = 12;

function ProgressLine({ answered }: { answered: number }) {
  const share = Math.min(answered / EXPECTED_ANSWERS, 1);
  return (
    <View style={styles.progress}>
      <View style={[styles.progressFill, { width: `${share * 100}%` }]} />
    </View>
  );
}

type Iv = ReturnType<typeof useInterview>;

function OwnWords({ onBegin }: { onBegin: (words: string) => void }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [words, setWords] = useState('');
  const examples = t('interview.examples', { returnObjects: true }) as unknown as string[];
  return (
    <ScrollView contentContainerStyle={styles.pad}>
      <Text variant="heading">{t('interview.ownWordsTitle')}</Text>
      <TextInput
        style={styles.field}
        value={words}
        onChangeText={setWords}
        placeholder={t('interview.ownWordsPlaceholder')}
        placeholderTextColor={theme.colors.textMuted}
        maxLength={200}
      />
      <View style={styles.chips}>
        {examples.map((e) => (
          <Chip key={e} label={e} onPress={() => setWords(e)} />
        ))}
      </View>
      <Button title={t('interview.begin')} disabled={!words.trim()} onPress={() => onBegin(words.trim())} />
    </ScrollView>
  );
}

function Chip({ label, onPress }: { label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.chip}>
      <Text variant="label">{label}</Text>
    </Pressable>
  );
}

function countLit(heard: Heard[]) {
  return heard.filter((h) => h.lit).length;
}

/** "What I've heard": eight parts that light up as answers come in; tap to unfold. */
function HeardStrip({ heard }: { heard: Heard[] }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [open, setOpen] = useState(false);
  const lit = countLit(heard);
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ expanded: open }} onPress={() => setOpen((o) => !o)} style={styles.strip}>
      <View style={styles.stripHead}>
        <Text variant="caption" muted style={styles.flex}>{t('interview.heardCount', { n: lit, of: heard.length })}</Text>
        <Ionicons name={open ? 'chevron-up' : 'chevron-down'} size={16} color={theme.colors.textMuted} />
      </View>
      {open ? (
        <Card style={styles.heardCard}>
          {heard.map((h) => (
            <View key={h.key} style={styles.heardRow}>
              <Text variant="label" muted style={styles.heardLabel}>{t(`interview.parts.${h.key}`)}</Text>
              <Text variant="label" muted={!h.lit} style={styles.heardValue}>{h.lit ? h.text : '—'}</Text>
            </View>
          ))}
        </Card>
      ) : (
        <View style={styles.parts}>
          {heard.map((h) => (
            <View key={h.key} style={[styles.part, h.lit && styles.partLit]}>
              <Text variant="caption" style={h.lit ? styles.partLitText : styles.partText}>{t(`interview.parts.${h.key}`)}</Text>
            </View>
          ))}
        </View>
      )}
    </Pressable>
  );
}

function Chat({ iv }: { iv: Iv }) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState('');
  // "Correct something" reopens the answer box, asking what to fix, until the Coach sends the picture again.
  const [correcting, setCorrecting] = useState(false);
  useEffect(() => {
    if (iv.step === 'picture') setCorrecting(false);
  }, [iv.step]);
  const list = useRef<ChatListHandle>(null);

  const submit = (text: string) => {
    const trimmed = text.trim();
    if (!trimmed) return;
    setDraft('');
    void iv.send(trimmed);
    list.current?.scrollToEnd();
  };
  const answering = iv.step === 'chat';

  return (
    <View style={styles.chat}>
      <ChatList
        ref={list}
        style={styles.list}
        contentContainerStyle={styles.listContent}
        data={iv.messages}
        keyExtractor={(m) => m.id}
        renderItem={({ item }) => (
          <View style={styles.row}>
            <Bubble mine={item.role === 'user'}>
              {item.role === 'user' ? <UserText text={item.text} /> : <CoachText id={item.id} text={item.text} live={item.status === 'sending'} />}
            </Bubble>
            {item.status === 'saved' ? (
              <Text variant="caption" muted style={styles.saved}>{t('interview.saved')}</Text>
            ) : null}
          </View>
        )}
        ListFooterComponent={
          iv.typing ? (
            <TypingDots label={t('interview.typing')} />
          ) : iv.step === 'shortName' ? (
            <ShortNameCard iv={iv} />
          ) : iv.step === 'picture' ? (
            <PictureCard iv={iv} onCorrect={() => {
              setCorrecting(true);
              iv.correct();
            }} />
          ) : undefined
        }
      />
      {iv.late ? (
        <View style={styles.pad}>
          <Banner message={t('interview.lateStrip')} action={t('interview.retry')} onPress={() => void iv.retry()} />
        </View>
      ) : null}
      {answering && iv.chips.length > 0 ? (
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          keyboardShouldPersistTaps="handled"
          style={styles.chipScroller}
          contentContainerStyle={styles.chipRow}
        >
          {iv.chips.map((c) => (
            <AnswerChip key={c} label={c} onPress={() => submit(c)} />
          ))}
        </ScrollView>
      ) : null}
      {answering ? (
        <Composer
          value={draft}
          onChangeText={setDraft}
          onSend={() => submit(draft)}
          placeholder={t(correcting ? 'interview.fixPlaceholder' : 'interview.answerPlaceholder')}
          sendLabel={t('interview.send')}
        />
      ) : null}
    </View>
  );
}

function fitsShortName(name: string) {
  return SHORT_NAME_MAX >= name.trim().length;
}

/** The short name, asked inline in the chat once the Coach has the picture. */
function ShortNameCard({ iv }: { iv: Iv }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [name, setName] = useState(iv.shortName);
  const [tooLong, setTooLong] = useState(false);
  const overLimit = !fitsShortName(name);
  return (
    <Card tone="accent">
      <Text variant="caption" muted>{t('interview.shortNameTitle')}</Text>
      <Text>{t('interview.shortNameBody', { name: name.trim() || iv.shortName })}</Text>
      <View style={styles.chips}>
        {iv.shortNameSuggestions.map((n) => (
          <Pressable key={n} accessibilityRole="button" accessibilityState={{ selected: n === name }} onPress={() => setName(n)}
            style={[styles.nameChip, n === name && styles.nameChipOn]}>
            <Text variant="label" style={n === name ? styles.nameChipOnText : undefined}>{n}</Text>
          </Pressable>
        ))}
      </View>
      <TextInput
        style={styles.nameInput}
        value={name}
        onChangeText={setName}
        maxLength={SHORT_NAME_MAX}
        placeholderTextColor={theme.colors.textMuted}
        accessibilityLabel={t('interview.shortNameTitle')}
      />
      {tooLong || overLimit ? <Text variant="label" style={styles.warnText}>{t('interview.shortNameTooLong')}</Text> : null}
      <Button title={t('interview.shortNameUse')} disabled={!name.trim()} onPress={() => setTooLong(!iv.chooseShortName(name))} style={styles.start} />
    </Card>
  );
}

/** The confirmable picture, inline under the Coach's last words: the paragraph, then what it sets up. */
function PictureCard({ iv, onCorrect }: { iv: Iv; onCorrect: () => void }) {
  const { t, i18n } = useTranslation();
  const [failed, setFailed] = useState(false);
  const p = iv.picture;
  if (!p) return null;
  const day = (iso: string) => formatDay(iso, { locale: i18n.language, weekday: false });
  const settle = settleForLine(t, p.settleForPicture, p.settleForDate ? day(p.settleForDate) : undefined);
  const confirm = () => {
    setFailed(false);
    iv.confirm().catch(() => setFailed(true));
  };
  const rows: [string, string | undefined][] = [
    [t('interview.rowShortName'), iv.shortName],
    [t('interview.rowHopedFor'), p.hopedForDate ? day(p.hopedForDate) : undefined],
    [t('interview.rowSettleFor'), settle],
    [t('interview.rowHours'), p.hoursPerWeek ? t('interview.rowHoursValue', { count: p.hoursPerWeek }) : undefined],
    [t('interview.rowSlots'), p.slots.length ? p.slots.join(', ') : undefined],
  ];
  return (
    <Card tone="accent">
      <Text variant="caption" muted>{t('interview.pictureTitle')}</Text>
      <Text>{p.text}</Text>
      {p.unknowns.length > 0 ? (
        <Text variant="label" muted style={styles.regular}>{t('interview.unknownsLine', { list: p.unknowns.join(' ') })}</Text>
      ) : null}
      <View style={styles.lines}>
        {rows
          .filter(([, v]) => v)
          .map(([label, value]) => (
            <View key={label} style={styles.line}>
              <Text variant="label" muted style={[styles.lineLabel, styles.regular]}>{label}</Text>
              <Text variant="label" style={styles.flex}>{value}</Text>
            </View>
          ))}
      </View>
      {p.slots.length ? <Text variant="label" muted style={styles.regular}>{t('interview.slotsNote')}</Text> : null}
      {failed ? <Text variant="label" style={styles.warnText}>{t('interview.confirmFailed')}</Text> : null}
      <View style={styles.pair}>
        <Button title={t('interview.thatsRight')} onPress={confirm} />
        <Button title={t('interview.correct')} kind="secondary" onPress={onCorrect} />
      </View>
    </Card>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  flex: { flex: 1 },
  leave: { color: theme.colors.accent },
  counter: { paddingHorizontal: theme.space.lg },
  pad: { padding: theme.space.lg, gap: theme.space.md },
  field: {
    padding: theme.space.md,
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
    color: theme.colors.text,
    ...theme.type.body,
  },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chipScroller: { flexGrow: 0 },
  chipRow: { alignItems: 'center', gap: theme.space.sm, paddingHorizontal: theme.space.lg, paddingVertical: theme.space.sm },
  chip: {
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
  top: { paddingHorizontal: theme.space.lg, paddingTop: theme.space.sm, gap: 6 },
  progress: { height: 4, borderRadius: 2, backgroundColor: theme.colors.surfaceAlt, overflow: 'hidden' },
  progressFill: { height: 4, borderRadius: 2, backgroundColor: theme.colors.accent },
  strip: { gap: 6, paddingBottom: theme.space.sm },
  stripHead: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  parts: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.xs },
  part: { paddingHorizontal: theme.space.sm, paddingVertical: 2, borderRadius: theme.radii.pill, backgroundColor: theme.colors.surfaceAlt },
  partLit: { backgroundColor: theme.colors.accentTint },
  partText: { color: theme.colors.textMuted },
  partLitText: { color: theme.colors.accent },
  heardCard: { gap: theme.space.xs, paddingVertical: 10 },
  heardRow: { flexDirection: 'row', gap: theme.space.sm },
  heardLabel: { width: 96, fontWeight: '400' },
  heardValue: { flex: 1, fontWeight: '400' },
  nameChip: { paddingHorizontal: theme.space.md, paddingVertical: 6, borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border },
  nameChipOn: { backgroundColor: theme.colors.accent, borderColor: theme.colors.accent },
  nameChipOnText: { color: theme.colors.onAccent },
  nameInput: { maxWidth: 200, paddingVertical: 6, borderBottomWidth: 1, borderColor: theme.colors.border, color: theme.colors.text, ...theme.type.body },
  warnText: { color: theme.colors.warn },
  start: { alignSelf: 'flex-start', marginTop: theme.space.xs },
  regular: { fontWeight: '400' },
  lines: { gap: 2, marginTop: theme.space.xs },
  line: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm, paddingVertical: theme.space.xs },
  lineLabel: { width: 92 },
  pair: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, marginTop: 6 },
  chat: { flex: 1 },
  list: { flex: 1 },
  listContent: { padding: theme.space.lg, gap: theme.space.sm },
  row: { gap: theme.space.xs },
  saved: { alignSelf: 'flex-end' },
}));
