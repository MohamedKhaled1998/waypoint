/** What the server says about progress. Pace, Projections and states are decided there; the phone only words them. */

export type PartOfMonth = { part: 'early' | 'mid' | 'late'; month: number };

export type Projection =
  | { kind: 'too_early' }
  | { kind: 'around' | 'between'; from: PartOfMonth; to: PartOfMonth };

export type ProgressState = 'too_early' | 'on_track' | 'slipping' | 'ahead' | 'past_settle_for';

export type MilestoneRow = {
  id: string;
  date: string;
  title: string;
  reachedWhen: string;
  kind: 'effort' | 'ability';
  state: 'upcoming' | 'reached' | 'nearly_there';
  youAreHere: boolean;
  pill: ProgressState | null;
  projection: Projection | null;
};

export type AspirationProgress = {
  aspirationId: string;
  shortName: string;
  hue: 'coral' | 'teal' | 'amber';
  ownWords: string;
  hopedForDate: string;
  settleForDate: string | null;
  settleForPicture: string | null;
  hasPlan: boolean;
  pace: number | null;
  paceWords: 'none' | 'full' | 'steady' | 'easing' | 'light';
  state: ProgressState;
  milestonesReached: number;
  milestonesTotal: number;
  next: MilestoneRow | null;
  milestones: MilestoneRow[];
};

export type WeekCard = {
  start: string;
  plannedMinutes: number;
  doneMinutes: number;
  line: 'kept_up' | 'some' | 'none' | 'nothing_planned';
  aspirations: { aspirationId: string; shortName: string; hue: 'coral' | 'teal' | 'amber'; plannedMinutes: number; doneMinutes: number }[];
};

export type ProgressView = { thisWeek: WeekCard; lastWeek: WeekCard; aspirations: AspirationProgress[] };

type T = (key: string, values?: Record<string, string | number>) => string;

/** "around mid-June", "somewhere between early and late June", or "too early to tell — you’re on the Plan so far". */
export function projectionLine(p: Projection, t: T): string {
  if (p.kind === 'too_early') return t('progress.tooEarlyLine');
  const month = (m: number) => t(`progress.months.${m}`);
  if (p.kind === 'around') return t('progress.around', { when: t(`progress.part.${p.from.part}Month`, { month: month(p.from.month) }) });
  if (p.from.month === p.to.month)
    return t('progress.betweenSameMonth', { from: t(`progress.part.${p.from.part}`), to: t(`progress.part.${p.to.part}`), month: month(p.from.month) });
  return t('progress.between', {
    from: t(`progress.part.${p.from.part}Month`, { month: month(p.from.month) }),
    to: t(`progress.part.${p.to.part}Month`, { month: month(p.to.month) }),
  });
}

export function hoursLabel(minutes: number): string {
  return String(Math.round((minutes / 60) * 10) / 10);
}

/** One flag per Milestone: reached or not, for the step bar. */
export function stepFractions(reached: number, total: number): boolean[] {
  return Array.from({ length: total }, (_, i) => i < reached);
}
