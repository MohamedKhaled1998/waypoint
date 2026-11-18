import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

export function Banner({ message }: { message: string }) {
  return (
    <View accessibilityRole="alert" style={styles.banner}>
      <Text variant="label">{message}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  banner: {
    padding: theme.space.md,
    borderRadius: theme.radii.sm,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.warningTint,
  },
}));
