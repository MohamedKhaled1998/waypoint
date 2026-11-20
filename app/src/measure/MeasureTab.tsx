import { useCallback, useState } from 'react';
import { TextInput, View } from 'react-native';
import { useFocusEffect } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useSession } from '../auth/SessionProvider';
import type { Hue } from '../theme/tokens';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Text } from '../ui/Text';
import { Section } from '../ui/Section';
import { Sheet } from '../ui/Sheet';
import { formatDay } from '../i18n/dates';
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

  const day = (iso: string, _long = false) => formatDay(iso, { locale: i18n.language, weekday: false });

  if (!view) return <Text muted>{failed ? t('measure.loadFailed') : t('common.loading')}</Text>;
  if (!view.hasMeasure) return <Text muted>{t('measure.none')}</Text>;

  const shares = barShares(view.readings, view.hopedFor);
  const lastReading = view.readings.at(-1);
  return (
    <View style={styles.tab}>
      <Section
        title={view.name}
        action={
          <Button kind="quiet" icon="add" title={t('measure.addReading')} style={styles.addButton} onPress={() => {
            setReadingId(newReadingId());
            setAdding(true);
          }} />
        }
      >
        <Card style={styles.box}>
          <Text variant="label" muted style={styles.regular}>
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
                    <View style={[styles.bar, styles.hueFill(hue), i < view.readings.length - 1 && styles.older, { height: `${Math.round((shares[i] ?? 0) * 100)}%` }]} />
                  </View>
                  <Text muted variant="caption">{day(r.date)}</Text>
                </View>
              ))}
            </View>
          )}
          <Text variant="label">{trendLine(view.trend, view.unit, view.trend.since ? day(view.trend.since) : '', (k, v) => t(k, v ?? {}))}</Text>
        </Card>
      </Section>
      <Text muted variant="caption">{t('measure.checkOnThePlan')}</Text>
      {adding ? (
        <AddReadingSheet
          label={`${view.name} (${view.unit})`}
          last={lastReading ? t('measure.lastReading', { value: lastReading.value, unit: view.unit, date: day(lastReading.date) }) : null}
          onClose={() => setAdding(false)}
          onSave={async (value) => {
            await httpMeasureApi(session).addReading(aspirationId, value, readingId);
            setAdding(false);
            load();
          }}
        />
      ) : null}
    </View>
  );
}

/** Whether an Aspiration has a Measure, so its screen can leave the tab out when it has none. */
export function useHasMeasure(aspirationId: string): boolean | null {
  const session = useSession();
  const [has, setHas] = useState<boolean | null>(null);
  useFocusEffect(
    useCallback(() => {
      httpMeasureApi(session)
        .get(aspirationId)
        .then((v) => setHas(v.hasMeasure))
        .catch(() => setHas(null));
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [aspirationId, session.status]),
  );
  return has;
}

function AddReadingSheet({ label, last, onClose, onSave }: { label: string; last: string | null; onClose: () => void; onSave: (value: number) => Promise<void> }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const [text, setText] = useState('');
  const [failed, setFailed] = useState(false);
  const value = parseReading(text);
  return (
    <Sheet onClose={onClose} label={t('common.close')}>
      <Text variant="heading">{t('measure.addReading')}</Text>
      <Text variant="label" muted style={styles.regular}>{label}</Text>
      {last ? <Text variant="label" muted style={styles.regular}>{last}</Text> : null}
      <TextInput
        style={styles.input}
        value={text}
        onChangeText={setText}
        keyboardType="decimal-pad"
        placeholder={t('measure.valuePlaceholder')}
        placeholderTextColor={theme.colors.textMuted}
        autoFocus
      />
      <Text variant="caption" muted>{t('measure.readingNote')}</Text>
      {failed ? <Text variant="label" style={styles.warn}>{t('measure.saveFailed')}</Text> : null}
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
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  tab: { gap: theme.space.md },
  box: { gap: theme.space.sm },
  regular: { fontWeight: '400' },
  warn: { color: theme.colors.warn },
  addButton: { minHeight: 32, paddingHorizontal: 0, paddingVertical: 2 },
  bars: { flexDirection: 'row', alignItems: 'flex-end', gap: theme.space.sm, height: 140 },
  barColumn: { flex: 1, height: '100%', alignItems: 'center', justifyContent: 'flex-end', gap: theme.space.xs },
  barTrack: { flex: 1, width: '60%', justifyContent: 'flex-end' },
  bar: { width: '100%', borderRadius: 3 },
  older: { opacity: 0.45 },
  hueFill: (hue: Hue) => ({ backgroundColor: theme.colors.hues[hue].solid }),
  input: { borderRadius: theme.radii.md, backgroundColor: theme.colors.surfaceAlt, padding: theme.space.md, color: theme.colors.text, fontSize: 18 },
}));
