import { View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

/** A small outlined label in a state colour (muted by default). */
export function Pill({ label, color }: { label: string; color?: string }) {
  const { theme } = useUnistyles();
  const fg = color ?? theme.colors.textMuted;
  return (
    <View style={[styles.pill, { borderColor: fg }]}>
      <Text variant="caption" style={{ color: fg }}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  pill: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.space.sm,
    paddingVertical: 2,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
  },
}));
