import { useEffect, useMemo, useRef, useState } from 'react';
import { FlatList, Pressable, TextInput, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useTranslation } from 'react-i18next';
import { useRouter } from 'expo-router';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { groupByDay, type ChatMessage } from '../../coach/chat';
import { useCoachChat } from '../../coach/useCoachChat';
import { useKeyboardHeight } from '../../coach/useKeyboardHeight';
import { Banner } from '../../ui/Banner';
import { Button } from '../../ui/Button';
import { Card } from '../../ui/Card';
import { Text } from '../../ui/Text';

type Row =
  | { key: string; kind: 'day'; label: string }
  | { key: string; kind: 'message'; message: ChatMessage }
  | { key: string; kind: 'typing' };

export default function CoachScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { theme } = useUnistyles();
  const insets = useSafeAreaInsets();
  const keyboard = useKeyboardHeight();
  const { messages, typing, late, send } = useCoachChat();
  const [draft, setDraft] = useState('');
  const list = useRef<FlatList<Row>>(null);

  const rows = useMemo<Row[]>(() => {
    const today = new Date();
    const yesterday = new Date(today.getTime() - 86_400_000);
    const key = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60_000).toISOString().slice(0, 10);
    const label = (day: string) =>
      day === key(today) ? t('coach.today') : day === key(yesterday) ? t('coach.yesterday') : new Date(`${day}T12:00:00`).toLocaleDateString();
    const out: Row[] = groupByDay(messages).flatMap((g) => [
      { key: `day-${g.day}`, kind: 'day' as const, label: label(g.day) },
      ...g.messages.map((message) => ({ key: message.id, kind: 'message' as const, message })),
    ]);
    if (typing) out.push({ key: 'typing', kind: 'typing' });
    return out;
  }, [messages, typing, t]);

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
      return (
        <Card hue={item.message.cardData.hue}>
          <Text variant="heading">{t('plan.proposalTitle', { name: item.message.cardData.shortName })}</Text>
          <Text muted>{t('plan.proposalBody')}</Text>
          <Button title={t('plan.review')} onPress={() => router.push({ pathname: '/plan-review', params: { id: versionId } })} />
        </Card>
      );
    }
    const mine = item.message.role === 'user';
    return (
      <View style={styles.row}>
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
    void send(text);
  };

  return (
    <View style={[styles.container, { paddingBottom: Math.max(keyboard, insets.bottom) }]}>
      <FlatList
        ref={list}
        style={styles.list}
        contentContainerStyle={styles.listContent}
        data={rows}
        keyExtractor={(r) => r.key}
        ListEmptyComponent={<Text muted style={styles.empty}>{t('coach.emptyBody')}</Text>}
        renderItem={({ item }) => renderRow(item)}
      />
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

const styles = StyleSheet.create((theme) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
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
