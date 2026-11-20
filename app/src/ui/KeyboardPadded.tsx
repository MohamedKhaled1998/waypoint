import { useRef, type ComponentRef, type ReactNode } from 'react';
import { Dimensions, View, type StyleProp, type ViewStyle } from 'react-native';
import { useReanimatedKeyboardAnimation } from 'react-native-keyboard-controller';
import Animated, { useAnimatedStyle, useSharedValue } from 'react-native-reanimated';

/**
 * Lifts its content above the keyboard, frame by frame as the keyboard moves. It pads by the keyboard's height less
 * whatever already sits between this view and the bottom of the screen (a tab bar), and never by less than
 * `minPadding` (the nav-bar inset on a screen that reaches the bottom edge).
 */
export function KeyboardPadded({ minPadding = 0, style, children }: { minPadding?: number; style?: StyleProp<ViewStyle>; children: ReactNode }) {
  const ref = useRef<ComponentRef<typeof View>>(null);
  const below = useSharedValue(0);
  const { height } = useReanimatedKeyboardAnimation();
  const measure = () =>
    ref.current?.measureInWindow((_x, y, _w, h) => {
      below.value = Math.max(0, Dimensions.get('screen').height - (y + h));
    });
  const lift = useAnimatedStyle(() => ({ paddingBottom: Math.max(minPadding, Math.abs(height.value) - below.value) }));
  return (
    <View ref={ref} onLayout={measure} style={style} collapsable={false}>
      <Animated.View style={[{ flex: 1 }, lift]}>{children}</Animated.View>
    </View>
  );
}
