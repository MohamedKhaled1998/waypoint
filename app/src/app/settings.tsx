import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';

export default function SettingsScreen() {
  const { t } = useTranslation();
  const { signOut } = useSession();
  return (
    <View style={styles.container}>
      <Card style={styles.card}>
        <Text variant="label" muted>
          {t('settings.account')}
        </Text>
        <Button kind="secondary" title={t('settings.signOut')} onPress={() => void signOut()} />
      </Card>
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { flex: 1, padding: theme.space.lg, paddingBottom: theme.space.lg + rt.insets.bottom },
  card: { gap: theme.space.md },
}));
