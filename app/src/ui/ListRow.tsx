import { Pressable, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

type Props = { title: string; icon?: keyof typeof Ionicons.glyphMap; onPress?: () => void };

/** A list row with its icon on a tinted tile. */
export function ListRow({ title, icon, onPress }: Props) {
  const { theme } = useUnistyles();
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.row}>
      {icon ? (
        <View style={styles.tile}>
          <Ionicons name={icon} size={18} color={theme.colors.accent} />
        </View>
      ) : null}
      <Text style={styles.title}>{title}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.md,
    paddingVertical: theme.space.md,
    paddingHorizontal: theme.space.lg,
    borderBottomWidth: 1,
    borderColor: theme.colors.border,
  },
  tile: {
    width: 32,
    height: 32,
    borderRadius: theme.radii.sm,
    backgroundColor: theme.colors.accentTint,
    alignItems: 'center',
    justifyContent: 'center',
  },
  title: { flex: 1 },
}));
