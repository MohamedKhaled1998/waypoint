import { Pressable, View, type ViewProps } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { Hue } from '../theme/tokens';

/** A card on tokens: surface, or tinted by an Aspiration's hue, or accent-toned. Pressable when given `onPress`. */
export function Card({ hue, tone, onPress, style, ...rest }: ViewProps & { hue?: Hue; tone?: 'accent'; onPress?: () => void }) {
  const card = <View {...rest} style={[styles.card, hue && styles.hue(hue), tone === 'accent' && styles.accent, style]} />;
  return onPress ? (
    <Pressable accessibilityRole="button" onPress={onPress} style={({ pressed }) => pressed && styles.pressed}>
      {card}
    </Pressable>
  ) : (
    card
  );
}

const styles = StyleSheet.create((theme) => ({
  card: {
    gap: 6,
    backgroundColor: theme.colors.surface,
    borderRadius: theme.radii.lg,
    borderWidth: 1,
    borderColor: theme.colors.border,
    padding: 14,
    ...theme.shadow,
  },
  hue: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].tint, borderColor: theme.colors.hues[hue].border }),
  accent: { backgroundColor: theme.colors.accentTint, borderColor: `${theme.colors.accent}33` },
  pressed: { opacity: 0.75 },
}));
