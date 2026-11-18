import { AppState } from 'react-native';
import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { useSession } from '../auth/SessionProvider';
import { phoneStorage } from '../sync/phone';
import { offlineCoachApi } from './offlineApi';
import { createCoachChat } from './chat';

/** The Coach thread for the signed-in user: stored messages, streaming replies, Saved messages retried on open. */
export function useCoachChat() {
  const session = useSession();
  const chat = useMemo(
    () =>
      createCoachChat(offlineCoachApi({ storage: phoneStorage, sync: session.phone.sync, now: () => new Date().toISOString() }), {
        newId: () => `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
        now: () => new Date().toISOString(),
      }),
    // The chat belongs to one sign-in; token functions are read at call time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.status],
  );
  const state = useSyncExternalStore(chat.subscribe, chat.getState);

  useEffect(() => {
    chat.load().then(() => chat.retrySaved()).catch(() => {});
    // A pull brought something in (the Coach's reply, say): read the thread again from the cache.
    const unsubscribe = session.phone.subscribe(() => void chat.load().catch(() => {}));
    // Coming back to the app is a good moment to try again.
    const sub = AppState.addEventListener('change', (s) => {
      if (s === 'active') void chat.retrySaved();
    });
    return () => {
      sub.remove();
      unsubscribe();
      chat.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chat]);

  return { ...state, send: chat.send, retry: chat.retrySaved };
}
