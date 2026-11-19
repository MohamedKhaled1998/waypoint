import type { ScheduledActionView } from './today';

/** The Coach's how-to for one Action; the server checks its numbers against the safe floors. */
export type Guide = {
  body: string;
  targets: { what: string; low: number; high: number; unit: string; why: string }[];
  withheld: { area: string; reason: string }[];
  /** The warm handoff to a professional. Lives only here, never in the body. */
  referral: string;
};

/** The How-to opens by itself only the first this-many times an Action is done. */
export const OPEN_BY_ITSELF_TIMES = 2;

export type GuideSheet = {
  guide: Guide;
  /** The How-to starts open; otherwise it is collapsed under a heading. */
  openByItself: boolean;
  /** The referral as its own card under the Guide, or null when it has already had its moment. */
  referralCard: string | null;
};

/**
 * What the Action sheet shows for the Action's Guide. `history` is the cached Actions: the times this Action (same
 * Aspiration and title) was Done before are counted from their Check-ins. The referral card goes with the automatic
 * opening, so it shows in the first two times and then stays in the Coach chat, where the server posts it once.
 */
export function guideSheet(action: ScheduledActionView, history: ScheduledActionView[]): GuideSheet | null {
  const guide = action.guide;
  if (!guide) return null;
  const timesDone = history.filter(
    (a) => a.id !== action.id && a.aspirationId === action.aspirationId && a.title === action.title && a.checkIn?.state === 'done',
  ).length;
  const openByItself = timesDone < OPEN_BY_ITSELF_TIMES;
  return { guide, openByItself, referralCard: openByItself && guide.referral.trim() ? guide.referral.trim() : null };
}
