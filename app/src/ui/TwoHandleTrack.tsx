import { useRef, useState } from 'react';
import { PanResponder, View, type LayoutChangeEvent } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { Handle, Span } from '../settings/tracks';

type Props = {
  /** The track's ends, in minutes. */
  from: number;
  to: number;
  value: Span;
  /** A handle dragged to this many minutes (not snapped yet: the caller's rule snaps and clamps). */
  onMove: (handle: Handle, minutes: number) => void;
  onRelease?: () => void;
  startLabel: string;
  endLabel: string;
};

const HANDLE = 28;

/** A track with a draggable handle at each end of a span. */
export function TwoHandleTrack({ from, to, value, onMove, onRelease, startLabel, endLabel }: Props) {
  const [width, setWidth] = useState(0);
  const latest = useRef({ width, value, from, to, onMove, onRelease });
  latest.current = { width, value, from, to, onMove, onRelease };
  const grab = useRef<{ handle: Handle; origin: number } | null>(null);

  const responder = (handle: Handle) =>
    PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,
      onPanResponderGrant: () => {
        grab.current = { handle, origin: latest.current.value[handle] };
      },
      onPanResponderMove: (_, g) => {
        const { width: w, from: lo, to: hi, onMove: move } = latest.current;
        if (!grab.current || w <= 0) return;
        move(handle, grab.current.origin + (g.dx / w) * (hi - lo));
      },
      onPanResponderRelease: () => latest.current.onRelease?.(),
      onPanResponderTerminate: () => latest.current.onRelease?.(),
    });
  const startPan = useRef(responder('start')).current;
  const endPan = useRef(responder('end')).current;

  const at = (minutes: number) => ((minutes - from) / (to - from)) * Math.max(0, width - HANDLE);

  return (
    <View style={styles.track} onLayout={(e: LayoutChangeEvent) => setWidth(e.nativeEvent.layout.width)}>
      <View style={styles.rail} />
      <View style={[styles.span, { start: at(value.start) + HANDLE / 2, width: at(value.end) - at(value.start) }]} />
      <View accessible accessibilityRole="adjustable" accessibilityLabel={startLabel} {...startPan.panHandlers} style={[styles.handle, { start: at(value.start) }]} />
      <View accessible accessibilityRole="adjustable" accessibilityLabel={endLabel} {...endPan.panHandlers} style={[styles.handle, { start: at(value.end) }]} />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  track: { height: HANDLE + theme.space.md, justifyContent: 'center' },
  rail: { height: 8, borderRadius: 4, backgroundColor: theme.colors.surfaceAlt },
  span: { position: 'absolute', height: 8, borderRadius: 4, backgroundColor: theme.colors.accent },
  handle: {
    position: 'absolute',
    width: HANDLE,
    height: HANDLE,
    borderRadius: HANDLE / 2,
    backgroundColor: theme.colors.surface,
    borderWidth: 3,
    borderColor: theme.colors.accent,
  },
}));
