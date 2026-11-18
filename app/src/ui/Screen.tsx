import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

/** A tab's placeholder content: a title and one explanatory line. */
export function EmptyState({ title, body }: { title: string; body: string }) {
  return (
    <View style={styles.container}>
      <Text variant="heading" style={styles.centered}>
        {title}
      </Text>
      <Text muted style={styles.centered}>
        {body}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    gap: theme.space.sm,
    padding: theme.space.xl,
    backgroundColor: theme.colors.background,
  },
  centered: { textAlign: 'center' },
}));
