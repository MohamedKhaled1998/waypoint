import { useState } from 'react';
import { Pressable, ScrollView, TextInput, View } from 'react-native';
import { Stack, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { firstRun } from '../firstrun/native';
import { Button } from '../ui/Button';
import { KeyboardPadded } from '../ui/KeyboardPadded';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

/** First-run step 2: the user's own words for the Aspiration, with example chips that fill the box. */
export default function AspirationScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const [words, setWords] = useState('');
  const examples = t('firstRun.reach.examples', { returnObjects: true }) as unknown as string[];

  async function next() {
    await firstRun.advance('interview');
    router.replace({ pathname: '/interview', params: { words: words.trim() } });
  }

  return (
    <KeyboardPadded minPadding={insets.bottom} style={styles.container}>
      <Stack.Screen options={{ headerShown: false }} />
      <ScrollView contentContainerStyle={styles.pad} keyboardShouldPersistTaps="handled">
        <StepCounter step="aspiration" />
        <Text variant="title">{t('interview.ownWordsTitle')}</Text>
        <Text muted>{t('firstRun.reach.intro')}</Text>
        <TextInput
          style={styles.field}
          value={words}
          onChangeText={setWords}
          placeholder={t('firstRun.reach.placeholder')}
          placeholderTextColor={theme.colors.textMuted}
          maxLength={200}
          multiline
        />
        <View style={styles.chips}>
          {examples.map((e) => (
            <Pressable key={e} accessibilityRole="button" onPress={() => setWords(e)} style={({ pressed }) => [styles.chip, pressed && styles.pressed]}>
              <Text variant="label" style={styles.regular}>{e}</Text>
            </Pressable>
          ))}
        </View>
      </ScrollView>
      <View style={styles.foot}>
        <Text variant="label" muted style={styles.regular}>{t('firstRun.reach.footer')}</Text>
        <Button title={t('firstRun.reach.start')} disabled={!words.trim()} onPress={() => void next()} />
      </View>
    </KeyboardPadded>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  pad: { padding: theme.space.xl, paddingTop: theme.space.xl + rt.insets.top, gap: theme.space.lg },
  regular: { fontWeight: '400' },
  pressed: { opacity: 0.7 },
  field: {
    minHeight: 110,
    padding: 14,
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    color: theme.colors.text,
    textAlignVertical: 'top',
    ...theme.type.body,
  },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: { paddingHorizontal: theme.space.md, paddingVertical: 6, borderRadius: theme.radii.pill, borderWidth: 1, borderColor: theme.colors.border },
  foot: { gap: theme.space.md, paddingHorizontal: theme.space.xl, paddingBottom: theme.space.lg },
}));
