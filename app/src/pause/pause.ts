import type { Sync } from '../sync/engine';
import type { Storage } from '../sync/storage';
import type { ScheduledActionView } from '../today/today';

/** A Pause the server told the phone about (active ones only), or one the user just started here. */
export type PauseView = {
  id: string;
  /** The phone's own ID for it: what Resume names it by, so it works before the server's ID is known. */
  clientId: string;
  /** Null: everything. */
  aspirationId: string | null;
  /** Set for "Just this Action". */
  actionId: string | null;
  startsAt: string;
  /** Exclusive: the midnight after the last paused day. Null: until resumed. */
  endsAt: string | null;
  startedBy: 'user' | 'coach';
};

export type PauseScope = { aspirationId?: string; actionId?: string };
/** "Not today" ends at midnight; "a few days" counts today as the first day. */
/** `{ on }`: the last paused day, "YYYY-MM-DD" (a Pause the Coach suggested from what the user said). */
export type PauseUntil = 'today' | 'open' | { days: number } | { on: string };

const pad = (n: number) => String(n).padStart(2, '0');
const localDate = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const addDays = (date: string, n: number) => {
  const [y, m, d] = date.split('-').map(Number);
  return localDate(new Date(y!, m! - 1, d! + n));
};
const dateOfIso = (iso: string) => iso.slice(0, 10);

/** The last day the Pause covers, "YYYY-MM-DD"; null when open. */
export function lastDay(p: PauseView): string | null {
  return p.endsAt ? addDays(dateOfIso(p.endsAt), -1) : null;
}

/** Is the Action inside the Pause? Same rule as the server: by day, never for an Action already checked in. */
export function setsAside(p: PauseView, a: ScheduledActionView): boolean {
  if (a.checkIn) return false;
  if (dateOfIso(p.startsAt) > a.date) return false;
  const last = lastDay(p);
  if (last !== null && a.date > last) return false;
  return p.actionId ? p.actionId === a.id : p.aspirationId === null || p.aspirationId === a.aspirationId;
}

/**
 * The user's Pauses and Resumes on the phone. Each is queued for the server with a client ID and shown at once: the
 * cached Actions it covers are marked set aside, which takes their reminders out of what is armed (the cache change
 * re-arms), and Resume puts them back. The server's next pull replaces all of this with its own answer.
 */
export function createOfflinePause(deps: { storage: Storage; sync: Sync; now: () => Date; newId: () => string }) {
  const { storage, sync } = deps;

  const active = async () => (await storage.list<{ pauses: PauseView[] }>('pauses'))[0]?.pauses ?? [];

  async function write(pauses: PauseView[]) {
    const actions = await storage.list<ScheduledActionView>('scheduled_action');
    return {
      upserts: [
        { kind: 'pauses' as const, id: 'active', data: { pauses } },
        ...actions
          .map((a) => ({ ...a, setAside: pauses.some((p) => setsAside(p, a)) }))
          .filter((a, i) => !!a.setAside !== !!actions[i]!.setAside)
          .map((a) => ({ kind: 'scheduled_action' as const, id: a.id, data: a })),
      ],
      removes: [],
    };
  }

  return {
    /** `clientId` is given when a card from the Coach starts it, so the same tap never starts two. */
    async start(scope: PauseScope, until: PauseUntil, clientId?: string) {
      const now = deps.now();
      const today = localDate(now);
      const endsOn =
        typeof until === 'object' ? ('on' in until ? until.on : addDays(today, Math.max(1, Math.floor(until.days)) - 1)) : until === 'today' ? today : null;
      const id = clientId ?? deps.newId();
      const known = clientId ? (await active()).find((p) => p.clientId === id) : undefined;
      if (known) return known;
      const pause: PauseView = {
        id, clientId: id, aspirationId: scope.aspirationId ?? null, actionId: scope.actionId ?? null,
        startsAt: now.toISOString(),
        endsAt: endsOn ? `${addDays(endsOn, 1)}T00:00:00.000Z` : null,
        startedBy: 'user',
      };
      const payload: Record<string, unknown> = { ...scope };
      if (until === 'open') payload.open = true;
      else if (endsOn && until !== 'today') payload.endsOn = endsOn;
      await sync.enqueue('pause', payload, id, await write([...(await active()), pause]));
      return pause;
    },
    async resume(pauseClientId: string) {
      await sync.enqueue('resume', { pauseClientId }, undefined, await write((await active()).filter((p) => p.clientId !== pauseClientId)));
    },
    pauses: active,
  };
}

export type Aspiration = { aspirationId: string; shortName: string; hue: string };
export type PausedSummary = { pauseId: string; until: string | null; byCoach: boolean };
export type PausedState = { everything: PausedSummary | null; aspirations: { aspirationId: string; pause: PausedSummary }[] };

const summary = (p: PauseView): PausedSummary => ({ pauseId: p.clientId, until: lastDay(p), byCoach: p.startedBy === 'coach' });

/** What is paused as a whole: everything, or single Aspirations. A Pause of one Action is not a state of its own. */
export function pausedState(pauses: PauseView[], aspirations: Aspiration[]): PausedState {
  const whole = pauses.filter((p) => !p.actionId);
  const everything = whole.find((p) => p.aspirationId === null);
  return {
    everything: everything ? summary(everything) : null,
    aspirations: whole
      .filter((p) => p.aspirationId !== null && aspirations.some((a) => a.aspirationId === p.aspirationId))
      .map((p) => ({ aspirationId: p.aspirationId!, pause: summary(p) })),
  };
}

export type TodayPausedLine =
  | { kind: 'everything'; until: string | null; byCoach: boolean; pauseId: string }
  | { kind: 'aspirations'; names: string[] }
  | null;

/** Today: everything paused (until a date, with Resume now), or one quiet line naming the paused Aspirations. */
export function todayPausedLine(state: PausedState, aspirations: Aspiration[]): TodayPausedLine {
  if (state.everything) return { kind: 'everything', ...state.everything };
  if (state.aspirations.length === 0) return null;
  return { kind: 'aspirations', names: state.aspirations.map((s) => aspirations.find((a) => a.aspirationId === s.aspirationId)?.shortName ?? '') };
}

export type ProgressRow = Aspiration & { paused: { pauseId: string; until: string | null } | null };

/** Progress: every Aspiration, a paused one carrying what its dashed row needs (its own Pause first, else everything's). */
export function progressRows(aspirations: Aspiration[], state: PausedState): ProgressRow[] {
  return aspirations.map((a) => {
    const own = state.aspirations.find((s) => s.aspirationId === a.aspirationId)?.pause ?? state.everything;
    return { ...a, paused: own ? { pauseId: own.pauseId, until: own.until } : null };
  });
}
