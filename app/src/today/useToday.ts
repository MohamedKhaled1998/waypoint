import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { phoneStorage } from '../sync/phone';
import { createCheckInTask } from './checkin';
import { createOfflineToday, type SlotView } from './offline';
import type { ScheduledActionView } from './today';

const pad = (n: number) => String(n).padStart(2, '0');
export function localNow(d = new Date()) {
  return { date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`, time: `${pad(d.getHours())}:${pad(d.getMinutes())}` };
}

export const actionById = (actions: ScheduledActionView[], id: string | undefined) => actions.find((a) => a.id === id);

const RETRY_MS = 30_000;

/**
 * The scheduled Actions from the phone's cache, the quick Check-in task with its Undo, and moves. Everything the user
 * does goes through the outbox, so it works with the server out of reach; a sync runs on focus and every 30 s.
 */
export function useToday() {
  const { phone } = useSession();
  const [actions, setActions] = useState<ScheduledActionView[]>([]);
  const [slots, setSlots] = useState<SlotView[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);
  const [undo, setUndo] = useState<{ scheduledActionId: string; state: string } | null>(null);
  const api = useMemo(() => createOfflineToday({ storage: phoneStorage, sync: phone.sync }), [phone]);
  const reload = useCallback(() => {
    Promise.all([api.actions(), api.slots()])
      .then(([a, s]) => {
        setActions(a);
        setSlots(s);
        setLoaded(true);
      })
      .catch(() => {});
  }, [api]);
  const task = useMemo(
    () =>
      createCheckInTask({
        api,
        now: () => new Date(),
        newId: () => `ci-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
        onError: () => setFailed(true),
        onChange: () => {
          setFailed(false);
          setUndo(task.undoable());
          reload();
        },
      }),
    [api, reload],
  );
  useEffect(() => phone.subscribe(reload), [phone, reload]);
  useFocusEffect(
    useCallback(() => {
      reload();
      void phone.sync.sync();
      const timer = setInterval(() => void phone.sync.sync(), RETRY_MS);
      return () => clearInterval(timer);
    }, [phone, reload]),
  );
  return { actions, slots, loaded, task, undo, failed, move: api.move, setHabitReminder: api.setHabitReminder };
}
