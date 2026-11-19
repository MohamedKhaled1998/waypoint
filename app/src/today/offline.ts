import type { Sync } from '../sync/engine';
import type { Storage } from '../sync/storage';
import type { CheckInApi } from './checkin';
import type { CheckInRequest, CheckInView, ScheduledActionView } from './today';

export type SlotView = { id: string; label: string; days: string; start: string; end: string };

/** What Today and the Action sheet read from the phone, and the user's own changes, which go through the outbox. */
export function createOfflineToday(deps: { storage: Storage; sync: Sync }) {
  const { storage, sync } = deps;

  async function change(id: string, edit: (a: ScheduledActionView) => ScheduledActionView) {
    const action = (await storage.list<ScheduledActionView>('scheduled_action')).find((a) => a.id === id);
    return action ? { upserts: [{ kind: 'scheduled_action' as const, id, data: edit(action) }], removes: [] } : { upserts: [], removes: [] };
  }

  const api: CheckInApi & { actions(): Promise<ScheduledActionView[]>; slots(): Promise<SlotView[]>; move(id: string, date: string, slotId: string): Promise<void>; setHabitReminder(action: ScheduledActionView, on: boolean, opts: { today: string; fromOffer?: boolean }): Promise<void> } = {
    actions: () => storage.list<ScheduledActionView>('scheduled_action'),
    slots: () => storage.list<SlotView>('slot'),

    async put(req: CheckInRequest): Promise<CheckInView> {
      const view: CheckInView = { state: req.state, minutes: req.minutes, reasons: req.reasons, note: req.note, tappedAt: req.tappedAt };
      const { clientId, ...payload } = req;
      await sync.enqueue('check_in', payload, clientId, await change(req.scheduledActionId, (a) => ({ ...a, checkIn: view })));
      return view;
    },

    async undo(scheduledActionId) {
      await sync.enqueue('undo_check_in', { scheduledActionId }, undefined, await change(scheduledActionId, (a) => ({ ...a, checkIn: null })));
    },

    /**
     * The user switches a habit's reminder on or off. The habit's coming Actions change in the cache at once; the server
     * applies it to the Plan, and `fromOffer` marks taking the Coach's offer to stop an Anchor habit's reminder.
     */
    async setHabitReminder(action, on, opts) {
      if (!action.habitId) return;
      const same = (await storage.list<ScheduledActionView>('scheduled_action')).filter(
        (a) => a.aspirationId === action.aspirationId && a.title === action.title && a.date >= opts.today,
      );
      await sync.enqueue('habit_reminder', { habitId: action.habitId, on, fromOffer: opts.fromOffer ?? false }, undefined, {
        upserts: same.map((a) => ({ kind: 'scheduled_action' as const, id: a.id, data: { ...a, reminderOn: on } })),
        removes: [],
      });
    },

    /** The user moves one Action to another Slot time. The Plan itself never changes on the phone. */
    async move(id, date, slotId) {
      const slot = (await storage.list<SlotView>('slot')).find((s) => s.id === slotId);
      if (!slot) return;
      await sync.enqueue(
        'move_action',
        { scheduledActionId: id, date, slotId },
        undefined,
        await change(id, (a) => ({ ...a, date, slot: slot.label, slotStart: slot.start, slotEnd: slot.end })),
      );
    },
  };
  return api;
}
