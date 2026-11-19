import { useCallback, useState } from 'react';
import { Modal, Pressable, TextInput, View } from 'react-native';
import { useFocusEffect } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import type { Hue } from '../theme/tokens';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import { httpMeasureApi } from './api';
import { barShares, parseReading, trendLine, type MeasureView } from './measure';

const newReadingId = () => `r-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;

/** The Measure tab: readings as bars, the trend in words and "Add reading". A check on the Plan, never a gate. */
export function MeasureTab({ aspirationId, hue }: { aspirationId: string; hue: Hue }) {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const [view, setView] = useState<MeasureView | null>(null);
  const [failed, setFailed] = useState(false);
  const [adding, setAdding] = useState(false);
  // One ID per opening of the sheet, so a retry after a lost response is the same reading to the server.
  const [readingId, setReadingId] = useState(newReadingId);

  const load = useCallback(() => {
    httpMeasureApi(session)
      .get(aspirationId)
      .then((v) => {
        setView(v);
        setFailed(false);
      })
      .catch(() => setFailed(true));
    // Token functions are read at call time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [aspirationId, session.status]);
  useFocusEffect(load);

  const day = (iso: string, long = false) =>
    new Date(`${iso}T00:00:00`).toLocaleDateString(i18n.language, long ? { day: 'numeric', month: 'short', year: 'numeric' } : { day: 'numeric', month: 'short' });

  if (!view) return <Text muted>{failed ? t('measure.loadFailed') : t('common.loading')}</Text>;
  if (!view.hasMeasure) return <Text muted>{t('measure.none')}</Text>;

  const shares = barShares(view.readings, view.hopedFor);
  return (
    <View style={styles.tab}>
      <Card style={styles.box}>
        <Text variant="label">{view.name}</Text>
        <Text muted variant="caption">
          {t('measure.startAndHoped', { start: view.start, hopedFor: view.hopedFor, unit: view.unit, date: day(view.hopedForDate, true) })}
        </Text>
        {view.readings.length === 0 ? (
          <Text muted>{t('measure.noReadings')}</Text>
        ) : (
          <View style={styles.bars}>
            {view.readings.map((r, i) => (
              <View key={r.id} style={styles.barColumn}>
                <Text variant="caption">{r.value}</Text>
                <View style={styles.barTrack}>
                  <View style={[styles.bar, styles.hueFill(hue), { height: `${Math.round((shares[i] ?? 0) * 100)}%` }]} />
                </View>
                <Text muted variant="caption">
                  {day(r.date)}
                </Text>
              </View>
            ))}
          </View>
        )}
        <Text>{trendLine(view.trend, view.unit, view.trend.since ? day(view.trend.since) : '', (k, v) => t(k, v ?? {}))}</Text>
      </Card>
      <Text muted variant="caption">
        {t('measure.checkOnThePlan')}
      </Text>
      <Button title={t('measure.addReading')} kind="secondary" onPress={() => {
          setReadingId(newReadingId());
          setAdding(true);
        }} />
      <AddReadingSheet
        visible={adding}
        label={`${view.name} (${view.unit})`}
        onClose={() => setAdding(false)}
        onSave={async (value) => {
          await httpMeasureApi(session).addReading(aspirationId, value, readingId);
          setAdding(false);
          load();
        }}
      />
    </View>
  );
}

function AddReadingSheet({ visible, label, onClose, onSave }: { visible: boolean; label: string; onClose: () => void; onSave: (value: number) => Promise<void> }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [text, setText] = useState('');
  const [failed, setFailed] = useState(false);
  const value = parseReading(text);
  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose}>
      <Pressable style={styles.scrim} onPress={onClose} accessibilityLabel={t('common.close')} />
      <View style={styles.sheet}>
        <Text variant="heading">{t('measure.addReading')}</Text>
        <Text muted>{label}</Text>
        <TextInput
          style={styles.input}
          value={text}
          onChangeText={setText}
          keyboardType="decimal-pad"
          placeholder={t('measure.valuePlaceholder')}
          placeholderTextColor={theme.colors.textMuted}
          autoFocus
        />
        {failed ? <Text muted>{t('measure.saveFailed')}</Text> : null}
        <Button
          title={t('measure.save')}
          disabled={value === null}
          onPress={() => {
            if (value === null) return;
            setFailed(false);
            onSave(value)
              .then(() => setText(''))
              .catch(() => setFailed(true));
          }}
        />
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create((theme) => ({
  tab: { gap: theme.space.md },
  box: { gap: theme.space.sm },
  bars: { flexDirection: 'row', alignItems: 'flex-end', gap: theme.space.sm, height: 140 },
  barColumn: { flex: 1, height: '100%', alignItems: 'center', justifyContent: 'flex-end', gap: theme.space.xs },
  barTrack: { flex: 1, width: '100%', justifyContent: 'flex-end' },
  bar: { width: '100%', borderRadius: theme.radii.sm },
  hueFill: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  scrim: { flex: 1, backgroundColor: theme.colors.scrim },
  sheet: { backgroundColor: theme.colors.surface, padding: theme.space.xl, gap: theme.space.md, borderTopStartRadius: theme.radii.lg, borderTopEndRadius: theme.radii.lg },
  input: { borderWidth: 1, borderColor: theme.colors.border, borderRadius: theme.radii.md, padding: theme.space.md, color: theme.colors.text, fontSize: 18 },
}));
