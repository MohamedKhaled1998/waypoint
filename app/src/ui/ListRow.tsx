import Ionicons from '@expo/vector-icons/Ionicons';
import type { ReactNode } from 'react';
import { Pressable, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Chevron } from './Chevron';
import { Text } from './Text';

type Props = {
  title: string;
  subtitle?: string;
  icon?: keyof typeof Ionicons.glyphMap;
  /** The icon tile's colour; accent by default. */
  tint?: string;
  end?: ReactNode;
  chevron?: boolean;
  onPress?: () => void;
  last?: boolean;
};

/** A list row with its icon on a tinted tile, an optional subtitle, something at the end and a chevron. */
export function ListRow({ title, subtitle, icon, tint, end, chevron = true, onPress, last }: Props) {
  const { theme } = useUnistyles();
  const color = tint ?? theme.colors.accent;
  return (
    <Pressable
      accessibilityRole={onPress ? 'button' : undefined}
      disabled={!onPress}
      onPress={onPress}
      style={({ pressed }) => [styles.row, last && styles.last, pressed && styles.pressed]}
    >
      {icon ? (
        <View style={[styles.tile, { backgroundColor: `${color}22` }]}>
          <Ionicons name={icon} size={19} color={color} />
        </View>
      ) : null}
      <View style={styles.body}>
        <Text>{title}</Text>
        {subtitle ? <Text variant="label" muted style={styles.subtitle}>{subtitle}</Text> : null}
      </View>
      {end}
      {chevron && onPress ? <Chevron /> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.space.md,
    paddingVertical: theme.space.md,
    borderBottomWidth: 1,
    borderColor: theme.colors.border,
  },
  last: { borderBottomWidth: 0 },
  pressed: { opacity: 0.7 },
  tile: { width: 36, height: 36, borderRadius: theme.radii.sm, alignItems: 'center', justifyContent: 'center' },
  body: { flex: 1, gap: 2 },
  subtitle: { fontWeight: '400' },
}));
