import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

export function Pill({ label }: { label: string }) {
  return (
    <View style={styles.pill}>
      <Text variant="caption">{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  pill: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.xs,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.accentTint,
  },
}));
