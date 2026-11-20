import type { ReactNode } from 'react';
import { Modal, Pressable, View } from 'react-native';
import { useReanimatedKeyboardAnimation } from 'react-native-keyboard-controller';
import Animated, { FadeIn, FadeOut, SlideInDown, SlideOutDown, useAnimatedStyle } from 'react-native-reanimated';
import { StyleSheet, UnistylesRuntime } from 'react-native-unistyles';

/**
 * A bottom sheet: handle, fading backdrop (tap to close), slides in, rides up with the keyboard and clears the nav
 * bar. Shown while mounted.
 */
export function Sheet({ children, onClose, label }: { children: ReactNode; onClose: () => void; label?: string }) {
  const { height } = useReanimatedKeyboardAnimation();
  const bottomInset = UnistylesRuntime.insets.bottom;
  const lift = useAnimatedStyle(() => {
    const keyboard = Math.abs(height.value);
    return { paddingBottom: 16 + Math.max(bottomInset, keyboard) };
  });
  return (
    <Modal transparent visible animationType="none" statusBarTranslucent navigationBarTranslucent onRequestClose={onClose}>
      <View style={styles.fill}>
        <Animated.View entering={FadeIn} exiting={FadeOut} style={[styles.fill, styles.backdrop]}>
          <Pressable accessibilityRole="button" accessibilityLabel={label} style={styles.fill} onPress={onClose} />
        </Animated.View>
        <Animated.View entering={SlideInDown.duration(260)} exiting={SlideOutDown} style={[styles.sheet, lift]} accessibilityViewIsModal>
          <View style={styles.handle} />
          {children}
        </Animated.View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  fill: { ...StyleSheet.absoluteFillObject },
  backdrop: { backgroundColor: theme.colors.scrim },
  sheet: {
    position: 'absolute',
    bottom: 0,
    start: 0,
    end: 0,
    maxHeight: rt.screen.height * 0.85,
    backgroundColor: theme.colors.surface,
    borderTopStartRadius: theme.radii.lg,
    borderTopEndRadius: theme.radii.lg,
    paddingHorizontal: 20,
    paddingTop: theme.space.sm,
    gap: theme.space.lg,
  },
  handle: { alignSelf: 'center', width: 40, height: 4, borderRadius: 2, backgroundColor: theme.colors.border, marginBottom: theme.space.xs },
}));
