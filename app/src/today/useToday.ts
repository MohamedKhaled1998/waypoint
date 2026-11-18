import { useFocusEffect } from 'expo-router';
import { useCallback, useMemo, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpTodayApi } from './api';
import { createCheckInTask } from './checkin';
import type { ScheduledActionView } from './today';

const pad = (n: number) => String(n).padStart(2, '0');
export function localNow(d = new Date()) {
  return { date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`, time: `${pad(d.getHours())}:${pad(d.getMinutes())}` };
}

export const actionById = (actions: ScheduledActionView[], id: string | undefined) => actions.find((a) => a.id === id);

/** The scheduled Actions, the quick Check-in task with its Undo, and a reload after each change. */
export function useToday() {
  const session = useSession();
  const [actions, setActions] = useState<ScheduledActionView[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);
  const [undo, setUndo] = useState<{ scheduledActionId: string; state: string } | null>(null);
  // Token functions are read at call time.
  const api = useMemo(() => httpTodayApi(session), [session.status]); // eslint-disable-line react-hooks/exhaustive-deps
  const reload = useCallback(() => {
    api
      .actions()
      .then((a) => {
        setActions(a);
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
  useFocusEffect(reload);
  return { actions, loaded, task, undo, failed };
}
