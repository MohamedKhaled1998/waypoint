import { Pressable, type PressableProps } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

type Kind = 'primary' | 'secondary' | 'quiet' | 'danger';

export function Button({ title, kind = 'primary', ...rest }: Omit<PressableProps, 'children'> & { title: string; kind?: Kind }) {
  styles.useVariants({ kind });
  return (
    <Pressable accessibilityRole="button" {...rest} style={styles.button}>
      <Text variant="label" style={styles.label}>
        {title}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  button: {
    minHeight: 48,
    paddingHorizontal: theme.space.xl,
    borderRadius: theme.radii.pill,
    alignItems: 'center',
    justifyContent: 'center',
    variants: {
      kind: {
        primary: { backgroundColor: theme.colors.accent },
        secondary: { backgroundColor: 'transparent', borderWidth: 1, borderColor: theme.colors.border },
        quiet: { backgroundColor: 'transparent' },
        danger: { backgroundColor: 'transparent', borderWidth: 1, borderColor: theme.colors.danger },
      },
    },
  },
  label: {
    variants: {
      kind: {
        primary: { color: theme.colors.onAccent },
        secondary: { color: theme.colors.text },
        quiet: { color: theme.colors.accent },
        danger: { color: theme.colors.danger },
      },
    },
  },
}));
