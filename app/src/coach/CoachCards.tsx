import Ionicons from '@expo/vector-icons/Ionicons';
import type { ReactNode } from 'react';
import { View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { formatDay } from '../i18n/dates';
import type { Hue } from '../theme/tokens';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { CoachText } from '../ui/chat/Chat';
import { Text } from '../ui/Text';
import type { ChatMessage } from './chat';

export const asHue = (hue: unknown): Hue | undefined => (hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined);

/** A card's head: an icon in the hue (or accent) and a label. */
export function CardHead({ icon, title, hue }: { icon: keyof typeof Ionicons.glyphMap; title: string; hue?: Hue }) {
  const { theme } = useUnistyles();
  return (
    <View style={styles.head}>
      <Ionicons name={icon} size={16} color={hue ? theme.colors.hues[hue].solid : theme.colors.accent} />
      <Text variant="label" style={styles.flex}>{title}</Text>
    </View>
  );
}

/** Between two rules: "Today", "Yesterday", a weekday this week, or the date. */
export function DaySeparator({ label }: { label: string }) {
  return (
    <View style={styles.day}>
      <View style={styles.rule} />
      <Text variant="caption" muted>{label}</Text>
      <View style={styles.rule} />
    </View>
  );
}

/** A Check-in the user made, echoed on their side of the thread. */
export function CheckInEcho({ message, hue }: { message: ChatMessage; hue?: Hue }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  const data = message.cardData ?? {};
  const result =
    data.state === 'done' ? t('coach.echo.done') : data.state === 'partly' ? t('coach.echo.partly', { minutes: data.minutes ?? 0 }) : t('coach.echo.skipped');
  return (
    <View style={styles.echo}>
      <Ionicons name="checkmark-done" size={14} color={hue ? theme.colors.hues[hue].solid : theme.colors.textMuted} />
      <Text variant="label" muted style={styles.regular}>{t('coach.echo.line', { title: data.title ?? message.text, result })}</Text>
    </View>
  );
}

/** The picture the Interview ended on, with a way to correct it in the thread. */
export function InterviewSummaryCard({ message, onCorrect }: { message: ChatMessage; onCorrect: () => void }) {
  const { t } = useTranslation();
  const data = message.cardData ?? {};
  const hue = asHue(data.hue);
  return (
    <Card hue={hue}>
      <CardHead icon="person-circle-outline" title={t('coach.summaryTitle', { name: data.shortName ?? '' })} hue={hue} />
      <Text>{data.picture ?? message.text}</Text>
      <Button kind="quiet" title={t('interview.correct')} onPress={onCorrect} style={styles.inlineButton} />
    </Card>
  );
}

export function GuideCard({ message }: { message: ChatMessage }) {
  const { t } = useTranslation();
  const data = message.cardData ?? {};
  const hue = asHue(data.hue);
  const areas = ((data.guide?.withheld ?? []) as { area: string }[]).map((w) => w.area).join(', ');
  return (
    <Card hue={hue}>
      <CardHead icon="book-outline" title={t('coach.guideTitle', { action: data.action })} hue={hue} />
      <CoachText id={message.id} text={String(data.guide?.body ?? '')} />
      {areas ? <Text variant="label" muted style={styles.regular}>{t('coach.guideWithheld', { areas })}</Text> : null}
    </Card>
  );
}

/** A warm pointer to a professional, set apart in the warn colour. */
export function ReferralCard({ message }: { message: ChatMessage }) {
  const { t } = useTranslation();
  const { theme } = useUnistyles();
  return (
    <View style={styles.referral}>
      <Ionicons name="medkit-outline" size={18} color={theme.colors.warn} />
      <View style={styles.flexGap}>
        <Text variant="label" style={styles.warnText}>{t('coach.referralTitle')}</Text>
        <Text>{message.text}</Text>
      </View>
    </View>
  );
}

/** A Measure reading that lags (new Milestone values to agree) or a Measure to rethink. */
export function MeasureCard({ message }: { message: ChatMessage }) {
  const { t, i18n } = useTranslation();
  const data = message.cardData ?? {};
  const resets = (data.resets ?? []) as { title: string; date: string; from: number; to: number }[];
  return (
    <Card tone="accent">
      <CardHead icon="pulse-outline" title={t(message.card === 'measure_rethink' ? 'coach.measureRethinkTitle' : 'coach.measureResetTitle', { name: data.name ?? '' })} />
      <CoachText id={message.id} text={message.text} />
      {resets.map((r) => (
        <Text key={`${r.title}-${r.date}`} variant="label" style={styles.regular}>
          {t('coach.measureResetLine', { title: r.title, date: formatDay(r.date, { locale: i18n.language }), from: r.from, to: r.to, unit: data.unit ?? '' })}
        </Text>
      ))}
    </Card>
  );
}

/** A proposal: accent-toned, with what it is and its state. */
export function ProposalCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card tone="accent">
      <CardHead icon="git-compare-outline" title={title} />
      {children}
    </Card>
  );
}

const styles = StyleSheet.create((theme) => ({
  flex: { flex: 1 },
  flexGap: { flex: 1, gap: 2 },
  regular: { fontWeight: '400' },
  head: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  day: { flexDirection: 'row', alignItems: 'center', gap: 10, marginVertical: theme.space.xs },
  rule: { flex: 1, height: 1, backgroundColor: theme.colors.border },
  echo: { flexDirection: 'row', alignItems: 'center', gap: 6, alignSelf: 'flex-end' },
  inlineButton: { alignSelf: 'flex-start', paddingHorizontal: 0, minHeight: 32, paddingVertical: 2 },
  referral: {
    flexDirection: 'row',
    gap: 10,
    padding: theme.space.md,
    borderRadius: theme.radii.md,
    borderWidth: 1,
    borderColor: `${theme.colors.warn}55`,
    backgroundColor: `${theme.colors.warn}10`,
  },
  warnText: { color: theme.colors.warn },
}));
