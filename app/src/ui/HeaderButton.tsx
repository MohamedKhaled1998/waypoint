import { Pressable } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';

type Props = { icon: keyof typeof Ionicons.glyphMap; label: string; onPress: () => void };

export function HeaderButton({ icon, label, onPress }: Props) {
  const { theme } = useUnistyles();
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} hitSlop={8} style={styles.button}>
      <Ionicons name={icon} size={22} color={theme.colors.text} />
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  button: { padding: theme.space.sm },
}));
