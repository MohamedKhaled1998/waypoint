import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { phoneStorage } from '../sync/phone';
import { createOfflinePause, pausedState, type Aspiration, type PauseView } from './pause';

/** The Pauses in force, from the phone's cache, with Pause and Resume going through the outbox. */
export function usePauses(aspirations: Aspiration[]) {
  const { phone } = useSession();
  const [pauses, setPauses] = useState<PauseView[]>([]);
  const api = useMemo(
    () => createOfflinePause({ storage: phoneStorage, sync: phone.sync, now: () => new Date(), newId: () => `pa-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}` }),
    [phone],
  );
  const reload = useCallback(() => {
    api.pauses().then(setPauses).catch(() => {});
  }, [api]);
  useEffect(() => {
    reload();
    return phone.subscribe(reload);
  }, [phone, reload]);
  return { pauses, state: pausedState(pauses, aspirations), start: api.start, resume: api.resume };
}
