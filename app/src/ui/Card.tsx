import { View, type ViewProps } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { Hue } from '../theme/tokens';

export function Card({ hue, style, ...rest }: ViewProps & { hue?: Hue }) {
  return <View {...rest} style={[styles.card, hue && styles.hue(hue), style]} />;
}

const styles = StyleSheet.create((theme) => ({
  card: {
    backgroundColor: theme.colors.surface,
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    padding: theme.space.lg,
    ...theme.shadow,
  },
  hue: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].tint }),
}));
