import { useState } from 'react';
import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { Text } from '../ui/Text';

export default function SignInScreen() {
  const { t } = useTranslation();
  const { signIn } = useSession();
  const [failed, setFailed] = useState(false);

  async function onPress() {
    setFailed(false);
    setFailed((await signIn()) === 'failed');
  }

  return (
    <View style={styles.container}>
      <View style={styles.hero}>
        <Text variant="title">{t('signIn.title')}</Text>
        <Text muted>{t('signIn.tagline')}</Text>
      </View>
      {failed ? <Banner message={t('signIn.failed')} /> : null}
      <Button title={t('signIn.continueWithGoogle')} onPress={() => void onPress()} />
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: {
    flex: 1,
    justifyContent: 'flex-end',
    gap: theme.space.lg,
    padding: theme.space.xl,
    paddingTop: theme.space.xl + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  hero: { flex: 1, justifyContent: 'center', gap: theme.space.sm },
}));
