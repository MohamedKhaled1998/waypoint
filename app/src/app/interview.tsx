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
import { Pill } from '../ui/Pill';
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
  // Confirming the picture hands the Plan to the Coach: first run waits on the drafting screen.
  const interviewDone = iv.step === 'done';
  useEffect(() => {
    if (!interviewDone || !inFirstRun) return;
    void firstRun.advance('drafting').then(() => router.replace('/drafting'));
  }, [interviewDone, inFirstRun, router]);

  const insets = useSafeAreaInsets();
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
        {iv.started && iv.step !== 'done' ? <HeardStrip heard={iv.heard} /> : null}
        {iv.started && iv.step === 'chat' ? <Chat iv={iv} /> : null}
        {iv.step === 'shortName' ? <ShortNameStep iv={iv} /> : null}
        {iv.step === 'picture' ? <PictureStep iv={iv} /> : null}
        {iv.step === 'done' ? <Done onClose={leave} /> : null}
      </View>
    </KeyboardPadded>
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

/** "What I've heard": eight parts that light up as answers come in; tap to unfold. */
function HeardStrip({ heard }: { heard: Heard[] }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  return (
    <Pressable accessibilityRole="button" onPress={() => setOpen((o) => !o)} style={styles.strip}>
      <Text variant="caption" muted>{t('interview.heard')}</Text>
      <View style={styles.chips}>
        {heard.map((h) => (
          <View key={h.key} style={[styles.part, h.lit && styles.partLit]}>
            <Text variant="caption" muted={!h.lit}>{t(`interview.parts.${h.key}`)}</Text>
          </View>
        ))}
      </View>
      {open
        ? heard.map((h) => (
            <View key={h.key} style={styles.partRow}>
              <Text variant="label">{t(`interview.parts.${h.key}`)}</Text>
              <Text muted={!h.lit}>{h.lit ? h.text : t('interview.heardNothing')}</Text>
            </View>
          ))
        : null}
    </Pressable>
  );
}

function Chat({ iv }: { iv: Iv }) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState('');
  const list = useRef<ChatListHandle>(null);

  const submit = (text: string) => {
    const trimmed = text.trim();
    if (!trimmed) return;
    setDraft('');
    void iv.send(trimmed);
    list.current?.scrollToEnd();
  };

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
        ListFooterComponent={iv.typing ? <TypingDots label={t('interview.typing')} /> : undefined}
      />
      {iv.late ? (
        <View style={styles.pad}>
          <Banner message={t('interview.lateStrip')} />
          <Button title={t('interview.retry')} kind="quiet" onPress={() => void iv.retry()} />
        </View>
      ) : null}
      {iv.chips.length > 0 ? (
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
      <Composer
        value={draft}
        onChangeText={setDraft}
        onSend={() => submit(draft)}
        placeholder={t('interview.answerPlaceholder')}
        sendLabel={t('interview.send')}
      />
    </View>
  );
}

function ShortNameStep({ iv }: { iv: Iv }) {
  const { t } = useTranslation();
  const [name, setName] = useState(iv.shortName);
  const [tooLong, setTooLong] = useState(false);
  const overLimit = name.trim().length > SHORT_NAME_MAX;
  function use() {
    setTooLong(!iv.chooseShortName(name));
  }
  return (
    <ScrollView contentContainerStyle={styles.pad}>
      <Text variant="heading">{t('interview.shortNameTitle')}</Text>
      <Text muted>{t('interview.shortNameBody')}</Text>
      <View style={styles.chips}>
        {iv.shortNameSuggestions.map((s) => (
          <Chip key={s} label={s} onPress={() => setName(s)} />
        ))}
      </View>
      <TextInput style={styles.field} value={name} onChangeText={setName} maxLength={SHORT_NAME_MAX + 8} />
      {tooLong || overLimit ? <Banner message={t('interview.shortNameTooLong')} /> : null}
      <Button title={t('interview.shortNameUse')} disabled={!name.trim()} onPress={use} />
    </ScrollView>
  );
}

function PictureStep({ iv }: { iv: Iv }) {
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
    [t('interview.rowHopedFor'), p.hopedForDate ? day(p.hopedForDate) : undefined],
    [t('interview.rowSettleFor'), settle || undefined],
    [t('interview.rowHours'), p.hoursPerWeek ? t('interview.rowHoursValue', { count: p.hoursPerWeek }) : undefined],
    [t('interview.rowSlots'), p.slots.length ? p.slots.join(', ') : undefined],
  ];
  return (
    <ScrollView contentContainerStyle={styles.pad}>
      <Pill label={iv.shortName} />
      <Text variant="heading">{t('interview.pictureTitle')}</Text>
      <Text>{p.text}</Text>
      {p.unknowns.length > 0 ? (
        <Card>
          <Text variant="label">{t('interview.unknownsTitle')}</Text>
          {p.unknowns.map((u) => (
            <Text key={u} muted>{u}</Text>
          ))}
        </Card>
      ) : null}
      <Card>
        {rows
          .filter(([, v]) => v)
          .map(([label, value]) => (
            <View key={label} style={styles.partRow}>
              <Text variant="label">{label}</Text>
              <Text>{value}</Text>
            </View>
          ))}
      </Card>
      {failed ? <Banner message={t('interview.confirmFailed')} /> : null}
      <Button title={t('interview.thatsRight')} onPress={confirm} />
      <Button title={t('interview.correct')} kind="secondary" onPress={iv.correct} />
    </ScrollView>
  );
}

function Done({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <View style={styles.pad}>
      <Text variant="heading">{t('interview.done')}</Text>
      <Text muted>{t('interview.doneBody')}</Text>
      <Button title={t('interview.close')} onPress={onClose} />
    </View>
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
  strip: {
    padding: theme.space.md,
    gap: theme.space.sm,
    borderBottomWidth: 1,
    borderBottomColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
  part: {
    paddingHorizontal: theme.space.sm,
    paddingVertical: theme.space.xs,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  partLit: { backgroundColor: theme.colors.accentTint, borderColor: theme.colors.accent },
  partRow: { gap: theme.space.xs, paddingVertical: theme.space.xs },
  chat: { flex: 1 },
  list: { flex: 1 },
  listContent: { padding: theme.space.lg, gap: theme.space.sm },
  row: { gap: theme.space.xs },
  saved: { alignSelf: 'flex-end' },
}));
