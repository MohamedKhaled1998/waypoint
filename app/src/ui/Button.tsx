import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, type PressableProps } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

type Kind = 'primary' | 'secondary' | 'quiet' | 'danger';

/** Pill buttons: primary (accent), secondary (soft accent), quiet (text only), danger (warn outline). */
export function Button({ title, kind = 'primary', icon, disabled, style, ...rest }: Omit<PressableProps, 'children' | 'style'> & {
  title: string; kind?: Kind; icon?: keyof typeof Ionicons.glyphMap; style?: PressableProps['style'];
}) {
  const { theme } = useUnistyles();
  styles.useVariants({ kind });
  const fg = { primary: theme.colors.onAccent, secondary: theme.colors.accent, quiet: theme.colors.textMuted, danger: theme.colors.warn }[kind];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: !!disabled }}
      disabled={disabled}
      {...rest}
      style={(state) => [styles.button, state.pressed && styles.pressed, disabled && styles.disabled, typeof style === 'function' ? style(state) : style]}
    >
      {icon ? <Ionicons name={icon} size={18} color={fg} /> : null}
      <Text variant="label" style={styles.label}>
        {title}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  button: {
    flexDirection: 'row',
    gap: theme.space.sm,
    minHeight: 44,
    paddingHorizontal: 18,
    paddingVertical: theme.space.md,
    borderRadius: theme.radii.pill,
    alignItems: 'center',
    justifyContent: 'center',
    variants: {
      kind: {
        primary: { backgroundColor: theme.colors.accent },
        secondary: { backgroundColor: theme.colors.accentTint },
        quiet: { backgroundColor: 'transparent' },
        danger: { backgroundColor: 'transparent', borderWidth: 1, borderColor: theme.colors.warn },
      },
    },
  },
  pressed: { opacity: 0.7 },
  disabled: { opacity: 0.4 },
  label: {
    fontSize: 14,
    variants: {
      kind: {
        primary: { color: theme.colors.onAccent },
        secondary: { color: theme.colors.accent },
        quiet: { color: theme.colors.textMuted },
        danger: { color: theme.colors.warn },
      },
    },
  },
}));
