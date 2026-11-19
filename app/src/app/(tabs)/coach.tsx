import { useEffect, useMemo, useRef, useState } from 'react';
import { FlatList, Pressable, TextInput, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useTranslation } from 'react-i18next';
import { useRouter } from 'expo-router';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { groupByDay, type ChatMessage } from '../../coach/chat';
import { chipLabel, filterThread, quickReplies, tagOf } from '../../coach/threads';
import { markThreadRead } from '../../coach/unread';
import { useThreadAspirations } from '../../coach/useCoachBadge';
import { useCoachChat } from '../../coach/useCoachChat';
import { pauseFromCard, waitingItems, type PauseCardData } from '../../coach/waiting';
import { usePauses } from '../../pause/usePauses';
import { usePlanSummaries } from '../../plan/usePlan';
import { useKeyboardHeight } from '../../coach/useKeyboardHeight';
import { Banner } from '../../ui/Banner';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { Text } from '../../ui/Text';

type Row =
  | { key: string; kind: 'day'; label: string }
  | { key: string; kind: 'message'; message: ChatMessage }
  | { key: string; kind: 'typing' };

const hasClientId = (clientId: string) => (p: { clientId: string }) => p.clientId === clientId;

export default function CoachScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { theme } = useUnistyles();
  const insets = useSafeAreaInsets();
  const keyboard = useKeyboardHeight();
  const { messages, typing, late, send } = useCoachChat();
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const waiting = useMemo(() => waitingItems(messages, plans, paused.pauses, new Date()), [messages, plans, paused.pauses]);
  const startPause = (card: PauseCardData) => {
    const { scope, until, clientId } = pauseFromCard(card);
    void paused.start(scope, until, clientId);
  };
  const isStarted = (card: PauseCardData) => paused.pauses.some(hasClientId(card.clientId));
  const review = (versionId: string) => router.push({ pathname: '/plan-review', params: { id: versionId } });
  const pauseWhen = (card: PauseCardData) =>
    card.span === 'open' ? t('coach.pauseOpen') : card.span === 'until' && card.endsOn ? t('coach.pauseUntil', { date: card.endsOn }) : t('coach.pauseToday');
  const [draft, setDraft] = useState('');
  const aspirations = useThreadAspirations();
  const [filter, setFilter] = useState<string | null>(null);
  const shown = filterThread(messages, filter);
  const replies = filter === null ? quickReplies(messages) : [];
  const list = useRef<FlatList<Row>>(null);

  const rows = useMemo<Row[]>(() => {
    const today = new Date();
    const yesterday = new Date(today.getTime() - 86_400_000);
    const key = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60_000).toISOString().slice(0, 10);
    const label = (day: string) =>
      day === key(today) ? t('coach.today') : day === key(yesterday) ? t('coach.yesterday') : new Date(`${day}T12:00:00`).toLocaleDateString();
    const out: Row[] = groupByDay(shown).flatMap((g) => [
      { key: `day-${g.day}`, kind: 'day' as const, label: label(g.day) },
      ...g.messages.map((message) => ({ key: message.id, kind: 'message' as const, message })),
    ]);
    if (typing) out.push({ key: 'typing', kind: 'typing' });
    return out;
  }, [messages, filter, typing, t]); // eslint-disable-line react-hooks/exhaustive-deps

  // Being on the thread reads it.
  useEffect(() => {
    markThreadRead(messages.at(-1)?.createdAt ?? new Date().toISOString());
  }, [messages]);

  useEffect(() => {
    list.current?.scrollToEnd({ animated: true });
  }, [rows.length, messages.at(-1)?.text]);

  const renderRow = (item: Row) => {
    if (item.kind === 'day') {
      return (
        <Text variant="caption" muted style={styles.day}>
          {item.label}
        </Text>
      );
    }
    if (item.kind === 'typing') {
      return (
        <View style={[styles.bubble, styles.coach]} accessibilityLabel={t('coach.typing')}>
          <Text muted>•••</Text>
        </View>
      );
    }
    if (item.message.card === 'proposal' && item.message.cardData?.planVersionId) {
      const versionId = String(item.message.cardData.planVersionId);
      const reason = item.message.cardData.reason;
      const kind = reason === 'time_budget_cut' ? 'cut' : reason === 're_plan' ? 'rePlan' : null;
      const name = item.message.cardData.shortName;
      return (
        <Card hue={item.message.cardData.hue}>
          <Text variant="heading">{kind ? t(`plan.${kind}Title`, { name }) : t('plan.proposalTitle', { name })}</Text>
          <Text muted>{kind ? t(`plan.${kind}Body`) : t('plan.proposalBody')}</Text>
          <Button title={t('plan.review')} onPress={() => review(versionId)} />
        </Card>
      );
    }
    if (item.message.card === 'pause_suggestion' && item.message.cardData?.clientId) {
      const card = item.message.cardData as PauseCardData;
      const started = isStarted(card);
      const when = pauseWhen(card);
      return (
        <Card>
          <Text variant="heading">{t('coach.pauseTitle')}</Text>
          <Text muted>
            {card.aspirationId ? t('coach.pauseOne', { name: card.shortName, when }) : t('coach.pauseEverything', { when })}
          </Text>
          {started ? <Text muted>{t('coach.pauseStarted')}</Text> : <Button title={t('coach.pauseStart')} onPress={() => startPause(card)} />}
        </Card>
      );
    }
    if (item.message.card === 'guide' && item.message.cardData?.guide) {
      const data = item.message.cardData;
      const areas = (data.guide.withheld ?? []).map(withheldArea).join(', ');
      return (
        <Card hue={data.hue}>
          <Text variant="heading">{t('coach.guideTitle', { action: data.action })}</Text>
          <Text>{data.guide.body}</Text>
          {areas ? <Text muted>{t('coach.guideWithheld', { areas })}</Text> : null}
        </Card>
      );
    }
    if (item.message.card === 'referral') {
      return (
        <Card hue={item.message.cardData?.hue}>
          <Text variant="heading">{t('coach.referralTitle')}</Text>
          <Text>{item.message.text}</Text>
        </Card>
      );
    }
    const mine = item.message.role === 'user';
    const tag = tagOf(item.message, aspirations);
    return (
      <View style={styles.row}>
        {tag ? (
          <View style={[styles.tag, mine && styles.tagMine]}>
            <View style={[styles.dot, tag.hue ? styles.hueDot(tag.hue) : null]} />
            <Text variant="caption" muted>{chipLabel(tag.label)}</Text>
          </View>
        ) : null}
        <View style={[styles.bubble, mine ? styles.mine : styles.coach]}>
          <Text style={mine ? styles.mineText : undefined}>{item.message.text}</Text>
        </View>
        {item.message.status === 'saved' ? (
          <Text variant="caption" muted style={styles.saved}>
            {t('coach.saved')}
          </Text>
        ) : null}
      </View>
    );
  };

  const submit = () => {
    const text = draft.trim();
    if (!text) return;
    setDraft('');
    void send(text, filter ?? undefined);
  };

  return (
    <View style={[styles.container, { paddingBottom: Math.max(keyboard, insets.bottom) }]}>
      {aspirations.length > 1 ? (
        <View style={styles.filters}>
          <FilterChip label={t('coach.filterAll')} selected={filter === null} onPress={() => setFilter(null)} />
          {aspirations.map((a) => (
            <FilterChip
              key={a.aspirationId}
              label={chipLabel(a.shortName)}
              hue={a.hue}
              selected={filter === a.aspirationId}
              accessibilityLabel={t('coach.filterLabel', { name: a.shortName })}
              onPress={() => setFilter(filter === a.aspirationId ? null : a.aspirationId)}
            />
          ))}
        </View>
      ) : null}
      {waiting.length > 0 ? (
        <View style={styles.pinned}>
          {waiting.map((w) => (
            <Pressable
              key={w.key}
              accessibilityRole="button"
              onPress={() => (w.kind === 'proposal' ? review(w.versionId) : startPause(w.card))}
              style={styles.pill}
            >
              <Text variant="label">
                {w.kind === 'proposal' ? t('coach.waitingProposal', { name: w.shortName }) : t('coach.waitingPause')}
              </Text>
            </Pressable>
          ))}
        </View>
      ) : null}
      <FlatList
        ref={list}
        style={styles.list}
        contentContainerStyle={styles.listContent}
        data={rows}
        keyExtractor={(r) => r.key}
        ListEmptyComponent={<Text muted style={styles.empty}>{t('coach.emptyBody')}</Text>}
        renderItem={({ item }) => renderRow(item)}
      />
      {replies.length > 0 ? (
        <View style={styles.replies}>
          {replies.map((r) => (
            <FilterChip key={r} label={r} selected={false} onPress={() => void send(r)} />
          ))}
        </View>
      ) : null}
      {late ? (
        <View style={styles.strip}>
          <Banner message={t('coach.lateStrip')} />
        </View>
      ) : null}
      <View style={styles.inputBar}>
        <TextInput
          style={styles.input}
          value={draft}
          onChangeText={setDraft}
          placeholder={t('coach.inputPlaceholder')}
          placeholderTextColor={theme.colors.textMuted}
          multiline
        />
        <Pressable accessibilityRole="button" accessibilityLabel={t('coach.send')} onPress={submit} style={styles.sendButton}>
          <Text variant="label" style={styles.sendText}>{t('coach.send')}</Text>
        </Pressable>
      </View>
    </View>
  );
}

function FilterChip({ label, selected, hue, onPress, accessibilityLabel }: {
  label: string; selected: boolean; hue?: string; onPress: () => void; accessibilityLabel?: string;
}) {
  const h = hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined;
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel ?? label} accessibilityState={{ selected }} onPress={onPress}
      style={[styles.chip, selected && styles.chipSelected]}>
      {h ? <View style={[styles.dot, styles.hueDot(h)]} /> : null}
      <Text variant="label" numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  flex: { flex: 1 },
  filters: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, paddingHorizontal: theme.space.lg, paddingTop: theme.space.md },
  chip: {
    flexDirection: 'row', alignItems: 'center', gap: theme.space.xs, maxWidth: 160,
    paddingHorizontal: theme.space.md, paddingVertical: theme.space.xs,
    borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border, backgroundColor: theme.colors.surface,
  },
  chipSelected: { borderColor: theme.colors.accent, backgroundColor: theme.colors.accentTint },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: theme.colors.textMuted },
  hueDot: (hue: 'coral' | 'teal' | 'amber') => ({ backgroundColor: theme.colors.hues[hue].solid }),
  tag: { flexDirection: 'row', alignItems: 'center', gap: theme.space.xs, alignSelf: 'flex-start' },
  tagMine: { alignSelf: 'flex-end' },
  replies: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm, paddingHorizontal: theme.space.lg, paddingBottom: theme.space.sm },
  container: { flex: 1, backgroundColor: theme.colors.background },
  pinned: { padding: theme.space.sm, gap: theme.space.xs, borderBottomWidth: 1, borderBottomColor: theme.colors.border, backgroundColor: theme.colors.surface },
  pill: { paddingHorizontal: theme.space.md, paddingVertical: theme.space.sm, borderRadius: theme.radii.pill, backgroundColor: theme.colors.background },
  list: { flex: 1 },
  listContent: { padding: theme.space.lg, gap: theme.space.sm },
  empty: { textAlign: 'center', marginTop: theme.space.xl },
  day: { textAlign: 'center', marginVertical: theme.space.sm },
  row: { gap: theme.space.xs },
  bubble: { maxWidth: '85%', padding: theme.space.md, borderRadius: theme.radii.md },
  mine: { alignSelf: 'flex-end', backgroundColor: theme.colors.accent },
  mineText: { color: theme.colors.onAccent },
  coach: { alignSelf: 'flex-start', backgroundColor: theme.colors.surface, ...theme.shadow },
  saved: { alignSelf: 'flex-end' },
  strip: { paddingHorizontal: theme.space.lg, paddingBottom: theme.space.sm },
  inputBar: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    gap: theme.space.sm,
    padding: theme.space.md,
    borderTopWidth: 1,
    borderTopColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
  input: {
    flex: 1,
    maxHeight: 120,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.background,
    color: theme.colors.text,
    ...theme.type.body,
  },
  sendButton: { paddingHorizontal: theme.space.lg, paddingVertical: theme.space.md, borderRadius: theme.radii.pill, backgroundColor: theme.colors.accent },
  sendText: { color: theme.colors.onAccent },
}));

function withheldArea(w: { area: string }): string {
  return w.area;
}
