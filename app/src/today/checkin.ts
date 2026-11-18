import type { CheckInRequest, CheckInState, CheckInView } from './today';

export const UNDO_MS = 4000;

export type CheckInApi = {
  put(req: CheckInRequest): Promise<CheckInView>;
  undo(scheduledActionId: string): Promise<void>;
};

type Undoable = { scheduledActionId: string; state: CheckInState };

/** The quick Check-in on Today: sent at once, with a 4-second Undo. */
export function createCheckInTask(deps: { api: CheckInApi; now: () => Date; newId: () => string; onChange?: () => void; onError?: () => void }) {
  let undoable: (Undoable & { before: CheckInView | null }) | null = null;
  let undoing = false;
  let timer: ReturnType<typeof setTimeout> | null = null;

  const close = () => {
    undoable = null;
    if (timer) clearTimeout(timer);
    timer = null;
    deps.onChange?.();
  };

  return {
    undoable: (): Undoable | null => (undoable ? { scheduledActionId: undoable.scheduledActionId, state: undoable.state } : null),
    async quick(action: { id: string; checkIn: CheckInView | null }, state: CheckInState) {
      try {
        await deps.api.put({
          scheduledActionId: action.id, clientId: deps.newId(), state, minutes: null, reasons: [], note: null, tappedAt: deps.now().toISOString(),
        });
      } catch {
        deps.onError?.();
        return;
      }
      if (timer) clearTimeout(timer);
      // A second tap inside the window keeps the state from before the first one; the caller's snapshot is stale by then.
      const before = undoable?.scheduledActionId === action.id ? undoable.before : action.checkIn;
      undoable = { scheduledActionId: action.id, state, before };
      timer = setTimeout(close, UNDO_MS);
      deps.onChange?.();
    },
    async submit(req: CheckInRequest) {
      await deps.api.put(req);
      deps.onChange?.();
    },
    async undo() {
      const u = undoable;
      if (!u || undoing) return;
      undoing = true;
      try {
        if (u.before) {
          // The earlier Check-in comes back as a newer tap, so the server keeps it.
          const { state, minutes, reasons, note } = u.before;
          await deps.api.put({ scheduledActionId: u.scheduledActionId, clientId: deps.newId(), state, minutes, reasons, note, tappedAt: deps.now().toISOString() });
        } else {
          await deps.api.undo(u.scheduledActionId);
        }
      } catch {
        // The Undo stays on offer until its window ends, so it can be tried again.
        deps.onError?.();
        return;
      } finally {
        undoing = false;
      }
      close();
    },
  };
}
