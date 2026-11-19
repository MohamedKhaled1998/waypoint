import { useState } from 'react';
import { ScrollView, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet } from 'react-native-unistyles';
import { usePlanSummaries } from '../plan/usePlan';
import { progressRows } from '../pause/pause';
import { usePauses } from '../pause/usePauses';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { HeaderButton } from '../ui/HeaderButton';
import { Text } from '../ui/Text';

const FEW_DAYS = [2, 3, 5, 7] as const;

/** One Aspiration: its overflow menu starts a Pause (three scopes) and a paused one shows a dashed Resume strip. */
export default function AspirationScreen() {
  const { t } = useTranslation();
  const { id } = useLocalSearchParams<{ id: string }>();
  const { plans } = usePlanSummaries();
  const paused = usePauses(plans);
  const [menu, setMenu] = useState<'closed' | 'open' | 'days'>('closed');
  const row = progressRows(plans, paused.state).find((r) => (r.aspirationId === id));
  if (!row) return null;
  const start = (until: Parameters<typeof paused.start>[1]) => {
    setMenu('closed');
    void paused.start({ aspirationId: row.aspirationId }, until);
  };
  return (
    <ScrollView contentContainerStyle={styles.content}>
      <Stack.Screen
        options={{
          title: row.shortName,
          headerRight: () => <HeaderButton icon="ellipsis-horizontal" label={t('pause.menu')} onPress={() => setMenu(menu === 'closed' ? 'open' : 'closed')} />,
        }}
      />
      {row.paused ? (
        <Card style={styles.dashed}>
          <Text>{row.paused.until ? t('pause.until', { date: row.paused.until }) : t('pause.open')}</Text>
          <Button title={t('pause.resume')} onPress={() => void paused.resume(row.paused!.pauseId)} />
        </Card>
      ) : null}
      {menu !== 'closed' && !row.paused ? (
        <Card>
          <Text variant="label">{t('pause.menuTitle', { name: row.shortName })}</Text>
          {menu === 'open' ? (
            <View style={styles.menu}>
              <Button kind="secondary" title={t('pause.scopeToday')} onPress={() => start('today')} />
              <Button kind="secondary" title={t('pause.scopeFewDays')} onPress={() => setMenu('days')} />
              <Button kind="secondary" title={t('pause.scopeOpen')} onPress={() => start('open')} />
            </View>
          ) : (
            <View style={styles.menu}>
              {FEW_DAYS.map((days) => (
                <Button key={days} kind="secondary" title={t('pause.days', { count: days })} onPress={() => start({ days })} />
              ))}
            </View>
          )}
        </Card>
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  content: { padding: theme.space.lg, gap: theme.space.md },
  dashed: { borderStyle: 'dashed', backgroundColor: 'transparent', gap: theme.space.md },
  menu: { gap: theme.space.sm, marginTop: theme.space.sm },
}));
