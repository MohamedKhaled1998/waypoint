import Ionicons from '@expo/vector-icons/Ionicons';
import { useEffect, useRef, useState } from 'react';
import { FlatList, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import { firstRun } from '../firstrun/native';
import { Banner } from '../ui/Banner';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

const CARDS = [
  { key: 'name', icon: 'flag-outline', hue: 'coral' },
  { key: 'chart', icon: 'chatbubbles-outline', hue: 'teal' },
  { key: 'bend', icon: 'git-branch-outline', hue: 'amber' },
] as const;

export default function SignInScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
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
      <FlatList
        style={styles.cards}
        onLayout={(e) => setWidth(e.nativeEvent.layout.width)}
        data={CARDS}
        keyExtractor={(c) => c.key}
        horizontal
        pagingEnabled
        showsHorizontalScrollIndicator={false}
        onMomentumScrollEnd={(e) => setPage(Math.round(e.nativeEvent.contentOffset.x / width))}
        renderItem={({ item }) => (
          <View style={[styles.hero, { width }]}>
            <View style={[styles.tile, { backgroundColor: `${theme.colors.hues[item.hue].solid}22` }]}>
              <Ionicons name={item.icon} size={36} color={theme.colors.hues[item.hue].solid} />
            </View>
            <Text variant="title">{t(`firstRun.cards.${item.key}.title`)}</Text>
            <Text muted>{t(`firstRun.cards.${item.key}.body`)}</Text>
          </View>
        )}
      />
      <View style={styles.pager} accessibilityLabel={t('firstRun.cardOf', { n: page + 1, of: CARDS.length })}>
        {CARDS.map((c, i) => (
          <View key={c.key} style={[styles.pagerDot, i === page && styles.pagerDotOn]} />
        ))}
      </View>
      <View style={styles.foot}>
        <StepCounter step="signIn" />
        {failed ? <Banner message={t('signIn.failed')} /> : null}
        <Button icon="logo-google" title={t('signIn.continueWithGoogle')} onPress={() => void onPress()} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: {
    flex: 1,
    paddingTop: 48 + rt.insets.top,
    paddingBottom: theme.space.xl + rt.insets.bottom,
    backgroundColor: theme.colors.background,
  },
  cards: { flex: 1 },
  hero: { justifyContent: 'center', gap: theme.space.lg, padding: theme.space.xl },
  tile: { width: 72, height: 72, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  pager: { flexDirection: 'row', gap: 6, alignSelf: 'center', marginBottom: theme.space.xl },
  pagerDot: { width: 8, height: 8, borderRadius: 4, backgroundColor: theme.colors.border },
  pagerDotOn: { width: 20, backgroundColor: theme.colors.accent },
  foot: { paddingHorizontal: theme.space.xl, gap: theme.space.md },
}));
