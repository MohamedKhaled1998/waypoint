import type { CheckInRequest, CheckInState } from './today';

export type FollowUp = 'feeling' | 'stoppedShort' | 'skipReason';

export const FEELING_CHIPS = ['great', 'ok', 'hard'] as const;
export const STOPPED_SHORT_CHIPS = ['tired', 'no_time', 'interrupted', 'unwell', 'hurt'] as const;
export const SKIP_CHIPS = ['no_time', 'tired', 'not_feeling_it', 'unwell', 'hurt', 'something_else'] as const;
/** Reasons after which the Coach says it will ask and ease the next days. */
const CARE_REASONS = ['hurt', 'unwell'];

/** 5-minute chips up to, but not including, the planned time. */
export function partlyChips(plannedMinutes: number): number[] {
  const chips: number[] = [];
  for (let m = 5; m < plannedMinutes || chips.length === 0; m += 5) chips.push(m);
  return chips;
}

export type SheetState = {
  step: 1 | 2;
  choice: CheckInState | null;
  followUp: FollowUp | null;
  reasons: string[];
  minutes: number | null;
  otherMinutes: string;
  note: string;
  showFreeText: boolean;
  coachWillAsk: boolean;
};

/** The Action sheet's two steps. Not today offers only "this Action" until Pause arrives. */
export function createActionSheet(action: { scheduledActionId: string; minutes: number }) {
  let s: SheetState = {
    step: 1, choice: null, followUp: null, reasons: [], minutes: null, otherMinutes: '', note: '', showFreeText: false, coachWillAsk: false,
  };
  const set = (patch: Partial<SheetState>) => {
    s = { ...s, ...patch };
    s.coachWillAsk = s.reasons.some((r) => CARE_REASONS.includes(r));
    s.showFreeText = s.reasons.includes('something_else');
  };
  const other = () => {
    const n = Number(s.otherMinutes);
    return /^\d+$/.test(s.otherMinutes.trim()) && n > 0 && n <= 1440 ? n : null;
  };

  return {
    notTodayScopes: ['thisAction'] as const,
    state: () => s,
    choose(choice: CheckInState) {
      set({
        step: 2,
        choice,
        followUp: choice === 'done' ? 'feeling' : choice === 'partly' ? 'stoppedShort' : 'skipReason',
        reasons: [], minutes: null, otherMinutes: '', note: '',
      });
    },
    back() {
      set({ step: 1, choice: null, followUp: null, reasons: [], minutes: null, otherMinutes: '', note: '' });
    },
    toggleReason(code: string) {
      set({ reasons: s.reasons.includes(code) ? s.reasons.filter((r) => r !== code) : [...s.reasons, code] });
    },
    pickMinutes(minutes: number) {
      set({ minutes, otherMinutes: '' });
    },
    setOtherMinutes(text: string) {
      set({ otherMinutes: text, minutes: null });
    },
    setNote(note: string) {
      set({ note });
    },
    /** The Check-in to send, or null while Partly has no minutes. */
    request(clientId: string, tappedAt: string): CheckInRequest | null {
      if (!s.choice) return null;
      const minutes = s.choice === 'partly' ? (s.minutes ?? other()) : null;
      if (s.choice === 'partly' && minutes === null) return null;
      return {
        scheduledActionId: action.scheduledActionId,
        clientId,
        state: s.choice,
        minutes,
        reasons: s.reasons,
        note: s.showFreeText && s.note.trim() ? s.note.trim() : null,
        tappedAt,
      };
    },
  };
}

export type MoveOption = { date: string; slotId: string; label: string; start: string };

const WEEKDAYS = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];
const addDays = (date: string, n: number) => {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};

/**
 * Where one Action can move to: each Slot on each of its days in the cached two weeks, later than now, other than where
 * it is. Only Slots the user already has; the phone never invents a time.
 */
export function moveOptions(
  action: { date: string; slotId?: string | null },
  slots: { id: string; label: string; days: string; start: string }[],
  now: { date: string; time: string },
  days = 14,
): MoveOption[] {
  const options: MoveOption[] = [];
  for (let i = 0; i < days; i++) {
    const date = addDays(now.date, i);
    const weekday = WEEKDAYS[new Date(`${date}T12:00:00Z`).getUTCDay()]!;
    for (const slot of [...slots].sort((a, b) => a.start.localeCompare(b.start))) {
      if (!slot.days.split(',').includes(weekday)) continue;
      if (date === now.date && slot.start <= now.time) continue;
      if (date === action.date && slot.id === action.slotId) continue;
      options.push({ date, slotId: slot.id, label: slot.label, start: slot.start });
    }
  }
  return options;
}
