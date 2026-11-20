import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

/** A soft accent strip with a sparkles icon and, optionally, an action at the end. */
export function Banner({ message, action, onPress }: { message: string; action?: string; onPress?: () => void }) {
  const { theme } = useUnistyles();
  const body = (
    <>
      <Ionicons name="sparkles-outline" size={18} color={theme.colors.accent} />
      <Text variant="label" style={styles.text}>{message}</Text>
      {action ? <Text variant="label" style={styles.action}>{action}</Text> : null}
    </>
  );
  return onPress ? (
    <Pressable accessibilityRole="button" onPress={onPress} style={({ pressed }) => [styles.banner, pressed && styles.pressed]}>
      {body}
    </Pressable>
  ) : (
    <View accessibilityRole="alert" style={styles.banner}>
      {body}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.sm,
    padding: theme.space.md,
    borderRadius: theme.radii.md,
    backgroundColor: theme.colors.accentTint,
  },
  text: { flex: 1, fontWeight: '400' },
  action: { color: theme.colors.accent },
  pressed: { opacity: 0.75 },
}));
