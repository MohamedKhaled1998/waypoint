import { usePathname } from 'expo-router';
import { useEffect, useRef, useSyncExternalStore, type ComponentRef, type ReactNode } from 'react';
import { I18nManager, useWindowDimensions, View, type StyleProp, type ViewStyle } from 'react-native';
import { useTranslation } from 'react-i18next';
import Animated, { FadeIn, FadeOut, useAnimatedStyle, useSharedValue, withRepeat, withTiming } from 'react-native-reanimated';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Button } from '../ui/Button';
import { Text } from '../ui/Text';
import { firstRun, useFirstRun } from './native';

type Rect = { x: number; y: number; w: number; h: number };

let target: Rect | null = null;
const listeners = new Set<() => void>();
const setTarget = (r: Rect | null) => {
  target = r;
  listeners.forEach((fn) => fn());
};

/** Wraps the Now card's quick buttons, so the tour can cut a hole around ✓. */
export function TourTarget({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const ref = useRef<ComponentRef<typeof View>>(null);
  useEffect(() => () => setTarget(null), []);
  return (
    <View
      ref={ref}
      collapsable={false}
      style={style}
      onLayout={() => ref.current?.measureInWindow((x, y, w, h) => setTarget({ x, y, w, h }))}
    >
      {children}
    </View>
  );
}

/**
 * The two-step Today tour over everything: step 1 dims the screen around the Now card's buttons with a pulsing ring
 * and waits for a real ✓; step 2 wraps up. "Skip tour" ends it at either step.
 */
export function TourOverlay() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const { width, height } = useWindowDimensions();
  const pathname = usePathname();
  const { step } = useFirstRun();
  const rect = useSyncExternalStore(
    (fn) => {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    () => target,
  );
  const pulse = useSharedValue(0);
  useEffect(() => {
    pulse.value = withRepeat(withTiming(1, { duration: 900 }), -1, true);
  }, [pulse]);
  const ring = useAnimatedStyle(() => ({ opacity: 0.5 + pulse.value * 0.5, transform: [{ scale: 1 + pulse.value * 0.06 }] }));

  const onToday = pathname === '/';
  const trying = step === 'tourTry' && !!rect;
  if (!onToday || !(trying || step === 'tourWrapUp')) return null;

  const pad = 6;
  // measureInWindow gives physical x; layout here runs in start/end, which flips in right-to-left.
  const startOf = (x: number, w: number) => (I18nManager.isRTL ? width - x - w : x);
  const r = trying && rect ? { x: startOf(rect.x, rect.w) - pad, y: rect.y - pad, w: rect.w + pad * 2, h: rect.h + pad * 2 } : null;
  const below = r ? r.y + r.h + 14 + 220 < height : true;
  const end = () => void firstRun.finishTour();
  return (
    <View style={[styles.fill, { width, height }]} pointerEvents="box-none">
      {r ? (
        <>
          <View style={[styles.dim, { top: 0, start: 0, width, height: r.y }]} />
          <View style={[styles.dim, { top: r.y + r.h, start: 0, width, bottom: 0 }]} />
          <View style={[styles.dim, { top: r.y, start: 0, width: r.x, height: r.h }]} />
          <View style={[styles.dim, { top: r.y, start: r.x + r.w, end: 0, height: r.h }]} />
          <Animated.View pointerEvents="none" style={[styles.ring, { top: r.y, start: r.x, width: r.w, height: r.h }, ring]} />
        </>
      ) : (
        <View style={[styles.dim, { top: 0, start: 0, width, height }]} />
      )}
      <Animated.View
        key={step}
        entering={FadeIn.duration(250)}
        exiting={FadeOut}
        style={[styles.tip, r ? (below ? { top: r.y + r.h + 14 } : { bottom: height - r.y + 14 }) : { top: height / 2 - 100 }]}
      >
        <Text variant="caption" muted>{t('firstRun.tour.counter', { n: r ? 1 : 2, of: 2 })}</Text>
        <Text variant="heading">{t(r ? 'firstRun.tour.tryTitle' : 'firstRun.tour.wrapTitle')}</Text>
        <Text muted>{t(r ? 'firstRun.tour.tryBody' : 'firstRun.tour.wrapBody')}</Text>
        <View style={styles.tipButtons}>
          <Button kind="quiet" title={t('firstRun.tour.skip')} onPress={end} style={styles.skip} />
          {r ? null : <Button title={t('firstRun.tour.done')} onPress={end} />}
        </View>
      </Animated.View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  fill: { position: 'absolute', top: 0, start: 0 },
  dim: { position: 'absolute', backgroundColor: theme.colors.scrim },
  ring: { position: 'absolute', borderRadius: theme.radii.lg, borderWidth: 2, borderColor: theme.colors.accent },
  tip: { position: 'absolute', start: 20, end: 20, gap: theme.space.sm, padding: theme.space.lg, borderRadius: theme.radii.lg, backgroundColor: theme.colors.surface },
  tipButtons: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginTop: theme.space.xs },
  skip: { paddingHorizontal: 0 },
}));
