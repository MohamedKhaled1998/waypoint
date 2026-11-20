import { useEffect, useMemo, useRef, useState } from 'react';
import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, ScrollView, View } from 'react-native';
import { KeyboardPadded } from '../../ui/KeyboardPadded';
import { useTranslation } from 'react-i18next';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { groupByDay, type ChatMessage } from '../../coach/chat';
import { chipLabel, filterThread, quickReplies } from '../../coach/threads';
import { asHue, CardHead, CheckInEcho, DaySeparator, GuideCard, InterviewSummaryCard, MeasureCard, ProposalCard, ReferralCard } from '../../coach/CoachCards';
import { parseDay } from '../../i18n/dates';
import type { Hue } from '../../theme/tokens';
import { useToday } from '../../today/useToday';
import { markThreadRead } from '../../coach/unread';
import { useThreadAspirations } from '../../coach/useCoachBadge';
import { useCoachChat } from '../../coach/useCoachChat';
import { pauseFromCard, waitingItems, type PauseCardData } from '../../coach/waiting';
import { usePauses } from '../../pause/usePauses';
import { usePlanSummaries } from '../../plan/usePlan';
import { AnswerChip, Bubble, ChatList, CoachText, Composer, TypingDots, UserText, type ChatListHandle } from '../../ui/chat/Chat';
import { formatDay } from '../../i18n/dates';
import { proposalState } from '../../coach/proposal';
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
  const { theme } = useUnistyles();
  const router = useRouter();
  const { actions, move } = useToday();
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
  const replies = quickReplies(messages);
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
    const out: Row[] = groupByDay(shown).flatMap((g) => [
      { key: `day-${g.day}`, kind: 'day' as const, label: dayLabel(g.day, new Date(), t, i18n.language) },
      ...g.messages.map((message) => ({ key: message.id, kind: 'message' as const, message })),
    ]);
    if (typing) out.push({ key: 'typing', kind: 'typing' });
    return out;
  }, [messages, filter, typing, t, i18n.language]); // eslint-disable-line react-hooks/exhaustive-deps

  // Being on the thread reads it.
  useEffect(() => {
    markThreadRead(messages.at(-1)?.createdAt ?? new Date().toISOString());
  }, [messages]);

  const hueOf = (aspirationId?: string | null) => asHue(aspirations.find((a) => a.aspirationId === aspirationId)?.hue);
  const lastId = messages.at(-1)?.id;
  const startFrom = (aspirationId: string | undefined, text: string, intent?: string) => {
    setScope(aspirationId ? { aspirationId, intent } : null);
    setDraft(text);
  };

  const renderMessage = (m: ChatMessage) => {
    const data = m.cardData ?? {};
    switch (m.card) {
      case 'check_in':
        return <CheckInEcho message={m} hue={hueOf(m.aspirationId)} />;
      case 'interview_summary':
        return <InterviewSummaryCard message={m} onCorrect={() => startFrom(m.aspirationId, t('coach.correctStarter', { name: data.shortName ?? '' }))} />;
      case 'guide':
        if (data.guide) return <GuideCard message={m} />;
        break;
      case 'referral':
        return <ReferralCard message={m} />;
      case 'measure_reset':
      case 'measure_rethink':
        return <MeasureCard message={m} />;
      case 'proposal': {
        if (!data.planVersionId) break;
        const versionId = String(data.planVersionId);
        const kind = data.reason === 'time_budget_cut' ? 'cut' : data.reason === 're_plan' ? 'rePlan' : null;
        const name = data.shortName;
        const state = proposalState(versionId, plansLoaded ? plans : null);
        return (
          <ProposalCard title={kind ? t(`plan.${kind}Title`, { name }) : t('plan.proposalTitle', { name })}>
            {state.kind === 'waiting' ? (
              <>
                <Text muted>{kind ? t(`plan.${kind}Body`) : t('plan.proposalBody')}</Text>
                <Button title={t('plan.review')} onPress={() => review(versionId)} style={styles.start} />
              </>
            ) : state.kind === 'approved' ? (
              <>
                <Text muted>
                  {state.approvedOn
                    ? t('plan.proposalApprovedOn', { date: formatDay(state.approvedOn, { locale: i18n.language, weekday: false }) })
                    : t('plan.proposalApproved')}
                </Text>
                <Button kind="secondary" icon="document-text-outline" title={t('plan.openPlan')} onPress={() => review(versionId)} style={styles.start} />
              </>
            ) : (
              <Text muted>{t('plan.proposalPast')}</Text>
            )}
          </ProposalCard>
        );
      }
      case 'pause_suggestion': {
        if (!data.clientId) break;
        const card = data as PauseCardData;
        const when = pauseWhen(card);
        return (
          <Card tone="accent">
            <CardHead icon="pause-circle-outline" title={t('coach.pauseTitle')} />
            <Text muted>{card.aspirationId ? t('coach.pauseOne', { name: card.shortName, when }) : t('coach.pauseEverything', { when })}</Text>
            {isStarted(card) ? (
              <Text muted>{t('coach.pauseStarted')}</Text>
            ) : (
              <Button title={t('coach.pauseStart')} onPress={() => startPause(card)} style={styles.start} />
            )}
          </Card>
        );
      }
      case 'settle_choice': {
        if (!data.aspirationId) break;
        const hue = asHue(data.hue);
        return (
          <Card hue={hue}>
            <CardHead icon="flag-outline" title={t('progress.settleChoice.title')} hue={hue} />
            <Text muted>{m.text}</Text>
            {(data.options as unknown[]).filter(isCoachIntent).map((option) => (
              <Button key={option} kind="secondary" title={t(`progress.settleChoice.options.${option}`)}
                onPress={() => startFrom(String(data.aspirationId), t(`coach.starters.${option}`, { name: data.shortName }), option)} />
            ))}
          </Card>
        );
      }
      case 'make_up': {
        const moved = isMoved(actions, data);
        return (
          <View style={styles.row}>
            <Bubble mine={false}>
              <CoachText id={m.id} text={m.text} live={isLive(m)} />
            </Bubble>
            {data.scheduledActionId && data.date && data.slotId ? (
              moved ? (
                <Text variant="caption" muted>{t('coach.makeUpDone', { day: formatDay(data.date, { locale: i18n.language }) })}</Text>
              ) : (
                <Button kind="secondary" icon="calendar-outline" style={styles.start}
                  title={t('coach.makeUp', { day: formatDay(data.date, { locale: i18n.language }) })}
                  onPress={() => void move(String(data.scheduledActionId), String(data.date), String(data.slotId))} />
              )
            ) : null}
          </View>
        );
      }
    }
    const mine = m.role === 'user';
    return (
      <View style={styles.row}>
        <Bubble mine={mine}>
          {mine ? <UserText text={m.text} /> : <CoachText id={m.id} text={m.text} live={isLive(m)} />}
        </Bubble>
        {m.status === 'saved' ? (
          <View style={styles.saved}>
            <Ionicons name="time-outline" size={12} color={theme.colors.textMuted} />
            <Text variant="caption" muted>{t('coach.saved')}</Text>
          </View>
        ) : null}
        {/* Quick replies sit under the Coach's last word, while it is the last word. */}
        {m.id === lastId && replies.length > 0 ? (
          <View style={styles.replies}>
            {replies.map((r) => (
              <AnswerChip key={r} label={r} onPress={() => { void send(r, filter ?? undefined); list.current?.scrollToEnd(); }} />
            ))}
          </View>
        ) : null}
      </View>
    );
  };

  const renderRow = (item: Row) => {
    if (item.kind === 'day') return <DaySeparator label={item.label} />;
    if (item.kind === 'typing') return <TypingDots label={t('coach.typing')} />;
    return renderMessage(item.message);
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
      {waiting.length > 0 || aspirations.length > 1 ? (
        <View style={styles.top}>
          {waiting.map((w) => (
            <Pressable
              key={w.key}
              accessibilityRole="button"
              onPress={() => (w.kind === 'proposal' ? review(w.versionId) : startPause(w.card))}
              style={({ pressed }) => [styles.pinned, pressed && styles.pressed]}
            >
              <Ionicons name="pin" size={14} color={theme.colors.accent} />
              <Text variant="label" style={styles.pinnedText}>
                {w.kind === 'proposal'
                  ? t(w.reason === 're_plan' ? 'coach.pinnedRePlan' : w.reason === 'time_budget_cut' ? 'coach.pinnedCut' : 'coach.pinnedPlan', { name: w.shortName })
                  : t('coach.pinnedPause')}
              </Text>
              <Text variant="label" style={styles.pinnedAction}>{w.kind === 'proposal' ? t('coach.waitingReview') : t('coach.pauseStartShort')}</Text>
            </Pressable>
          ))}
          {aspirations.length > 1 ? (
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.filters}>
              <FilterChip label={t('coach.filterAll')} selected={filter === null} onPress={() => setFilter(null)} />
              {aspirations.map((a) => (
                <FilterChip
                  key={a.aspirationId}
                  label={chipLabel(a.shortName)}
                  hue={asHue(a.hue)}
                  selected={filter === a.aspirationId}
                  accessibilityLabel={t('coach.filterLabel', { name: a.shortName })}
                  onPress={() => setFilter(filter === a.aspirationId ? null : a.aspirationId)}
                />
              ))}
            </ScrollView>
          ) : null}
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
      {late ? (
        <View style={styles.offline}>
          <Ionicons name="cloud-offline-outline" size={16} color={theme.colors.textMuted} />
          <Text variant="label" muted style={styles.offlineText}>{t('coach.lateStrip')}</Text>
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

/** A make-up offer is taken once its Action sits on the offered day. */
function isMoved(actions: { id: string; date: string }[], offer: Record<string, unknown>): boolean {
  return actions.some((a) => a.id === offer.scheduledActionId && a.date === offer.date);
}

/** "Today", "Yesterday", a weekday within the last week, or the date. */
function dayLabel(day: string, now: Date, t: (k: string) => string, locale: string): string {
  const d = parseDay(day);
  if (!d) return day;
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 12);
  const daysAgo = Math.round((today.getTime() - d.getTime()) / 86_400_000);
  if (daysAgo === 0) return t('coach.today');
  if (daysAgo === 1) return t('coach.yesterday');
  if (daysAgo > 1 && daysAgo < 7) return new Intl.DateTimeFormat(locale, { weekday: 'long' }).format(d);
  return formatDay(day, { locale, today: now });
}

function FilterChip({ label, selected, hue, onPress, accessibilityLabel }: {
  label: string; selected: boolean; hue?: Hue; onPress: () => void; accessibilityLabel?: string;
}) {
  const { theme } = useUnistyles();
  const color = hue ? theme.colors.hues[hue].solid : theme.colors.accent;
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel ?? label} accessibilityState={{ selected }} onPress={onPress}
      style={[styles.chip, selected && { backgroundColor: `${color}26`, borderColor: color }]}>
      {hue ? <View style={[styles.dot, { backgroundColor: color }]} /> : null}
      <Text variant="label" numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  top: { gap: theme.space.sm, paddingHorizontal: theme.space.lg, paddingTop: theme.space.sm, paddingBottom: 10, borderBottomWidth: 1, borderColor: theme.colors.border },
  pinned: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm, padding: 10, borderRadius: theme.radii.md, backgroundColor: theme.colors.accentTint },
  pinnedText: { flex: 1, fontWeight: '400' },
  pinnedAction: { color: theme.colors.accent, fontWeight: '700' },
  pressed: { opacity: 0.75 },
  filters: { gap: theme.space.sm },
  chip: {
    flexDirection: 'row', alignItems: 'center', gap: 6, maxWidth: 180,
    paddingHorizontal: theme.space.md, paddingVertical: 6,
    borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border,
  },
  dot: { width: 8, height: 8, borderRadius: 4 },
  list: { flex: 1 },
  listContent: { padding: theme.space.lg, gap: theme.space.md },
  empty: { textAlign: 'center', marginTop: theme.space.xl },
  row: { gap: theme.space.sm },
  start: { alignSelf: 'flex-start' },
  saved: { flexDirection: 'row', alignItems: 'center', gap: 4, alignSelf: 'flex-end' },
  replies: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  offline: { flexDirection: 'row', alignItems: 'center', gap: theme.space.sm, paddingHorizontal: 14, paddingVertical: theme.space.sm, backgroundColor: theme.colors.surfaceAlt },
  offlineText: { flex: 1, fontWeight: '400' },
}));
