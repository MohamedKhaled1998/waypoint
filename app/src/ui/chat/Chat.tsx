import Ionicons from '@expo/vector-icons/Ionicons';
import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState, type ReactElement } from 'react';
import { FlatList, Pressable, TextInput, View, type FlatListProps } from 'react-native';
import Animated, { useAnimatedStyle, useSharedValue, withDelay, withRepeat, withSequence, withTiming } from 'react-native-reanimated';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from '../Text';
import { parseMarkdown, type Span } from './markdown';
import { nextReveal } from './reveal';

/**
 * A chat list, newest at the bottom. It is inverted, so new and growing messages stay in view on their own while the
 * user is at the bottom, and someone reading back is never pulled away. Takes messages oldest first.
 */
export type ChatListHandle = { scrollToEnd: () => void };

function ChatListInner<T>({ data, ListFooterComponent, ...props }: FlatListProps<T>, ref: React.Ref<ChatListHandle>) {
  const list = useRef<FlatList<T>>(null);
  useImperativeHandle(ref, () => ({
    scrollToEnd: () => list.current?.scrollToOffset({ offset: 0, animated: true }),
  }));
  const newestFirst = useMemo(() => (data ? Array.from(data).reverse() : data), [data]);
  return (
    <FlatList
      ref={list}
      inverted
      data={newestFirst}
      // Inverted, the header sits at the bottom: where the typing dots belong.
      ListHeaderComponent={ListFooterComponent}
      keyboardShouldPersistTaps="handled"
      keyboardDismissMode="interactive"
      {...props}
    />
  );
}

export const ChatList = forwardRef(ChatListInner) as <T>(props: FlatListProps<T> & { ref?: React.Ref<ChatListHandle> }) => ReactElement;

/**
 * The Coach's words with the small Markdown subset. A live reply (streaming, or one that arrived while the chat was
 * open) is revealed a few characters at a time, once; the rest of the thread shows whole.
 */
export function CoachText({ id, text, live = false }: { id: string; text: string; live?: boolean }) {
  const shown = useReveal(id, text, live);
  const blocks = parseMarkdown(text.slice(0, shown));
  return (
    <View style={styles.blocks}>
      {blocks.map((b, i) => (
        <View key={i} style={b.kind === 'paragraph' ? undefined : styles.listRow}>
          {b.kind === 'bullet' ? <Text>•</Text> : b.kind === 'numbered' ? <Text>{b.n}.</Text> : null}
          <Text style={b.kind === 'paragraph' ? undefined : styles.listText}>
            {b.spans.map((s, j) => (
              <Text key={j} style={spanStyle(s)}>{s.text}</Text>
            ))}
          </Text>
        </View>
      ))}
    </View>
  );
}

const spanStyle = (s: Span) => (s.bold && s.italic ? styles.boldItalic : s.bold ? styles.bold : s.italic ? styles.italic : undefined);

const TICK_MS = 30;
/** Replies already revealed, so a row the list re-creates on scroll doesn't play it again. */
const revealed = new Set<string>();

function useReveal(id: string, text: string, live: boolean) {
  const [shown, setShown] = useState(live && !revealed.has(id) ? 0 : text.length);
  const target = useRef(text.length);
  target.current = text.length;
  const behind = shown < text.length;
  useEffect(() => {
    if (!behind) {
      if (text.length > 0) revealed.add(id);
      return;
    }
    const timer = setInterval(() => setShown((s) => nextReveal(s, target.current)), TICK_MS);
    return () => clearInterval(timer);
  }, [behind, id, text.length]);
  return Math.min(shown, text.length);
}

export function Bubble({ mine, children }: { mine: boolean; children: React.ReactNode }) {
  return <View style={[styles.bubble, mine ? styles.mine : styles.coach]}>{children}</View>;
}

export function UserText({ text }: { text: string }) {
  return <Text style={styles.mineText}>{text}</Text>;
}

/** Three dots that rise and fade in turn while the Coach is thinking. */
export function TypingDots({ label }: { label: string }) {
  return (
    <View style={[styles.bubble, styles.coach, styles.typing]} accessibilityLabel={label} accessibilityRole="progressbar">
      {[0, 1, 2].map((i) => (
        <Dot key={i} delay={i * 160} />
      ))}
    </View>
  );
}

function Dot({ delay }: { delay: number }) {
  const t = useSharedValue(0);
  useEffect(() => {
    t.value = withDelay(delay, withRepeat(withSequence(withTiming(1, { duration: 320 }), withTiming(0, { duration: 320 }), withTiming(0, { duration: 240 })), -1));
  }, [delay, t]);
  const style = useAnimatedStyle(() => ({ opacity: 0.35 + t.value * 0.65, transform: [{ translateY: -3 * t.value }] }));
  return <Animated.View style={[styles.dot, style]} />;
}

/** The pill input and round send button at the foot of a chat. */
export function Composer({ value, onChangeText, onSend, placeholder, sendLabel }: {
  value: string; onChangeText: (s: string) => void; onSend: () => void; placeholder: string; sendLabel: string;
}) {
  const { theme } = useUnistyles();
  const empty = !value.trim();
  return (
    <View style={styles.composer}>
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={theme.colors.textMuted}
        multiline
      />
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={sendLabel}
        accessibilityState={{ disabled: empty }}
        disabled={empty}
        onPress={onSend}
        style={({ pressed }) => [styles.send, empty && styles.sendOff, pressed && styles.pressed]}
      >
        <Ionicons name="arrow-up" size={20} color={theme.colors.onAccent} />
      </Pressable>
    </View>
  );
}

/** A quick answer: its own height, pill-shaped, accent outline. */
export function AnswerChip({ label, onPress }: { label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={({ pressed }) => [styles.chip, pressed && styles.pressed]}>
      <Text variant="label" style={styles.chipText}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  blocks: { gap: theme.space.sm },
  listRow: { flexDirection: 'row', gap: theme.space.sm },
  listText: { flexShrink: 1 },
  bold: { fontWeight: '700' },
  italic: { fontStyle: 'italic' },
  boldItalic: { fontWeight: '700', fontStyle: 'italic' },
  bubble: { maxWidth: '85%', paddingHorizontal: theme.space.md + 2, paddingVertical: theme.space.sm + 2, borderRadius: theme.radii.lg },
  mine: { alignSelf: 'flex-end', backgroundColor: theme.colors.accent, borderBottomEndRadius: 4 },
  mineText: { color: theme.colors.onAccent },
  coach: { alignSelf: 'flex-start', backgroundColor: theme.colors.surfaceAlt, borderBottomStartRadius: 4 },
  typing: { flexDirection: 'row', gap: 5, paddingVertical: theme.space.md + 2 },
  dot: { width: 7, height: 7, borderRadius: 4, backgroundColor: theme.colors.textMuted },
  composer: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    gap: theme.space.sm,
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderTopWidth: 1,
    borderTopColor: theme.colors.border,
    backgroundColor: theme.colors.background,
  },
  input: {
    flex: 1,
    maxHeight: 120,
    minHeight: 44,
    paddingHorizontal: theme.space.lg,
    paddingTop: 11,
    paddingBottom: 11,
    borderRadius: 22,
    backgroundColor: theme.colors.surfaceAlt,
    color: theme.colors.text,
    ...theme.type.body,
  },
  send: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center', backgroundColor: theme.colors.accent },
  sendOff: { opacity: 0.4 },
  pressed: { opacity: 0.7 },
  chip: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: `${theme.colors.accent}66`,
    backgroundColor: theme.colors.background,
  },
  chipText: { color: theme.colors.accent },
}));
