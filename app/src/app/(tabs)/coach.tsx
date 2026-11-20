import { useEffect, useMemo, useRef, useState } from 'react';
import { Pressable, View } from 'react-native';
import { KeyboardPadded } from '../../ui/KeyboardPadded';
import { useTranslation } from 'react-i18next';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { StyleSheet } from 'react-native-unistyles';
import { groupByDay, type ChatMessage } from '../../coach/chat';
import { chipLabel, filterThread, quickReplies, tagOf } from '../../coach/threads';
import { markThreadRead } from '../../coach/unread';
import { useThreadAspirations } from '../../coach/useCoachBadge';
import { useCoachChat } from '../../coach/useCoachChat';
import { pauseFromCard, waitingItems, type PauseCardData } from '../../coach/waiting';
import { usePauses } from '../../pause/usePauses';
import { usePlanSummaries } from '../../plan/usePlan';
import { AnswerChip, Bubble, ChatList, CoachText, Composer, TypingDots, UserText, type ChatListHandle } from '../../ui/chat/Chat';
import { formatDay } from '../../i18n/dates';
import { proposalState } from '../../coach/proposal';
import { Banner } from '../../ui/Banner';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { isCoachIntent } from '../../progress/progress';
import type { CoachContext } from '../../coach/chat';
import { Text } from '../../ui/Text';

type Row =
  | { key: string; kind: 'day'; label: string }
  | { key: string; kind: 'message'; message: ChatMessage }
  | { key: string; kind: 'typing' };

const hasClientId = (clientId: string) => (p: { clientId: string }) => p.clientId === clientId;

export default function CoachScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const { messages, typing, late, send } = useCoachChat();
  const { plans, loaded: plansLoaded } = usePlanSummaries();
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
  const params = useLocalSearchParams<{ aspirationId?: string; intent?: string; shortName?: string }>();
  // Opened from an Aspiration: the message starts from what the user chose, and they decide whether to send it.
  // The Aspiration the next message is about, and what the user chose: sent with the message so the Coach has the context.
  const [scope, setScope] = useState<CoachContext | null>(null);
  const list = useRef<ChatListHandle>(null);
  // What the thread held when it first loaded is history; a Coach message that arrives after that is live.
  const history = useRef<Set<string> | null>(null);
  if (history.current === null && messages.length > 0) history.current = new Set(messages.map((m) => m.id));
  const isLive = (m: ChatMessage) => m.status === 'sending' || (history.current !== null && !history.current.has(m.id));
  useEffect(() => {
    if (!params.aspirationId || !isCoachIntent(params.intent)) return;
    setScope({ aspirationId: params.aspirationId, intent: params.intent });
    setDraft(t(`coach.starters.${params.intent}`, { name: params.shortName ?? '' }));
  }, [params.aspirationId, params.intent, params.shortName, t]);

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
        <TypingDots label={t('coach.typing')} />
      );
    }
    if (item.message.card === 'proposal' && item.message.cardData?.planVersionId) {
      const versionId = String(item.message.cardData.planVersionId);
      const reason = item.message.cardData.reason;
      const kind = reason === 'time_budget_cut' ? 'cut' : reason === 're_plan' ? 'rePlan' : null;
      const name = item.message.cardData.shortName;
      const state = proposalState(versionId, plansLoaded ? plans : null);
      return (
        <Card hue={item.message.cardData.hue}>
          <Text variant="heading">{kind ? t(`plan.${kind}Title`, { name }) : t('plan.proposalTitle', { name })}</Text>
          {state.kind === 'waiting' ? (
            <>
              <Text muted>{kind ? t(`plan.${kind}Body`) : t('plan.proposalBody')}</Text>
              <Button title={t('plan.review')} onPress={() => review(versionId)} />
            </>
          ) : state.kind === 'approved' ? (
            <>
              <Text muted>
                {state.approvedOn
                  ? t('plan.proposalApprovedOn', { date: formatDay(state.approvedOn, { locale: i18n.language, weekday: false }) })
                  : t('plan.proposalApproved')}
              </Text>
              <Button kind="secondary" title={t('plan.openPlan')} onPress={() => review(versionId)} />
            </>
          ) : (
            <Text muted>{t('plan.proposalPast')}</Text>
          )}
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
    if (item.message.card === 'settle_choice' && item.message.cardData?.aspirationId) {
      const data = item.message.cardData;
      return (
        <Card hue={data.hue}>
          <Text variant="heading">{t('progress.settleChoice.title')}</Text>
          <Text muted>{item.message.text}</Text>
          {(data.options as unknown[]).filter(isCoachIntent).map((option) => (
            <Button key={option} kind="secondary" title={t(`progress.settleChoice.options.${option}`)}
              onPress={() => {
                setScope({ aspirationId: String(data.aspirationId), intent: option });
                setDraft(t(`coach.starters.${option}`, { name: data.shortName }));
              }} />
          ))}
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
        <Bubble mine={mine}>
          {mine ? <UserText text={item.message.text} /> : <CoachText id={item.message.id} text={item.message.text} live={isLive(item.message)} />}
        </Bubble>
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
    setScope(null);
    // A message opened from an Aspiration's button is about that Aspiration; otherwise the filter's.
    void send(text, scope?.aspirationId ?? filter ?? undefined, scope?.intent);
    list.current?.scrollToEnd();
  };

  return (
    <KeyboardPadded style={styles.container}>
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
      {rows.length === 0 ? (
        // Outside the inverted list, which would draw an empty state upside down.
        <View style={styles.list}>
          <Text muted style={styles.empty}>{t('coach.emptyBody')}</Text>
        </View>
      ) : (
        <ChatList
          ref={list}
          style={styles.list}
          contentContainerStyle={styles.listContent}
          data={rows}
          keyExtractor={(r) => r.key}
          renderItem={({ item }) => renderRow(item)}
        />
      )}
      {replies.length > 0 ? (
        <View style={styles.replies}>
          {replies.map((r) => (
            <AnswerChip key={r} label={r} onPress={() => { void send(r); list.current?.scrollToEnd(); }} />
          ))}
        </View>
      ) : null}
      {late ? (
        <View style={styles.strip}>
          <Banner message={t('coach.lateStrip')} />
        </View>
      ) : null}
      <Composer
        value={draft}
        onChangeText={setDraft}
        onSend={submit}
        placeholder={t('coach.inputPlaceholder')}
        sendLabel={t('coach.send')}
      />
    </KeyboardPadded>
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
  saved: { alignSelf: 'flex-end' },
  strip: { paddingHorizontal: theme.space.lg, paddingBottom: theme.space.sm },
}));

function withheldArea(w: { area: string }): string {
  return w.area;
}
