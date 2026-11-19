import { useEffect, useRef, useState } from 'react';
import { FlatList, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import { firstRun } from '../firstrun/native';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

const CARDS = ['name', 'chart', 'bend'] as const;

export default function SignInScreen() {
  const { t } = useTranslation();
  const { signIn } = useSession();
  // One card fills the list, whatever the screen and padding.
  const [width, setWidth] = useState(0);
  const [failed, setFailed] = useState(false);
  const [page, setPage] = useState(0);
  const started = useRef(false);

  // A signed-out phone showing the cards is where the first run begins.
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void firstRun.load().then(() => firstRun.start()).catch(() => {});
  }, []);

  async function onPress() {
    setFailed(false);
    setFailed((await signIn()) === 'failed');
  }

  return (
    <View style={styles.container}>
      <StepCounter step="signIn" />
      <FlatList
        style={styles.cards}
        onLayout={(e) => setWidth(e.nativeEvent.layout.width)}
        data={CARDS}
        keyExtractor={(c) => c}
        horizontal
        pagingEnabled
        showsHorizontalScrollIndicator={false}
        onMomentumScrollEnd={(e) => setPage(Math.round(e.nativeEvent.contentOffset.x / width))}
        renderItem={({ item }) => (
          <View style={[styles.hero, { width }]}>
            <Text variant="title">{t(`firstRun.cards.${item}.title`)}</Text>
            <Text muted>{t(`firstRun.cards.${item}.body`)}</Text>
          </View>
        )}
      />
      <Text variant="caption" muted>
        {t('firstRun.cardOf', { n: page + 1, of: CARDS.length })}
      </Text>
      {failed ? <Banner message={t('signIn.failed')} /> : null}
      <Button title={t('signIn.continueWithGoogle')} onPress={() => void onPress()} />
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: {
    flex: 1,
    gap: theme.space.lg,
    padding: theme.space.xl,
    paddingTop: theme.space.xl + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  cards: { flex: 1 },
  hero: { justifyContent: 'center', gap: theme.space.sm },
}));
