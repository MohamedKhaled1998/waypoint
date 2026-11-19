import { useState } from 'react';
import { Pressable, ScrollView, TextInput, View } from 'react-native';
import { Stack, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { firstRun } from '../firstrun/native';
import { Button } from '../ui/Button';
import { StepCounter } from '../ui/StepCounter';
import { Text } from '../ui/Text';

/** First-run step 2: the user's own words for the Aspiration, with example chips that fill the box. */
export default function AspirationScreen() {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const router = useRouter();
  const [words, setWords] = useState('');
  const examples = t('interview.examples', { returnObjects: true }) as unknown as string[];

  async function next() {
    await firstRun.advance('interview');
    router.replace({ pathname: '/interview', params: { words: words.trim() } });
  }

  return (
    <ScrollView contentContainerStyle={styles.pad} style={styles.container}>
      <Stack.Screen options={{ headerShown: false }} />
      <StepCounter step="aspiration" />
      <Text variant="heading">{t('interview.ownWordsTitle')}</Text>
      <TextInput
        style={styles.field}
        value={words}
        onChangeText={setWords}
        placeholder={t('interview.ownWordsPlaceholder')}
        placeholderTextColor={theme.colors.textMuted}
        maxLength={200}
      />
      <View style={styles.chips}>
        {examples.map((e) => (
          <Pressable key={e} accessibilityRole="button" onPress={() => setWords(e)} style={styles.chip}>
            <Text variant="label">{e}</Text>
          </Pressable>
        ))}
      </View>
      <Button title={t('interview.begin')} disabled={!words.trim()} onPress={() => void next()} />
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme, rt) => ({
  container: { flex: 1, backgroundColor: theme.colors.background },
  pad: { padding: theme.space.lg, paddingTop: theme.space.lg + rt.insets.top, gap: theme.space.md },
  field: {
    padding: theme.space.md,
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
    color: theme.colors.text,
    ...theme.type.body,
  },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.space.sm },
  chip: {
    paddingHorizontal: theme.space.md,
    paddingVertical: theme.space.sm,
    borderRadius: theme.radii.pill,
    borderWidth: 1,
    borderColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
}));
