import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpInterviewApi } from './api';
import { createInterview } from './interview';

/** The Interview for the signed-in user. Opening it resumes any unfinished one. */
export function useInterview() {
  const session = useSession();
  const iv = useMemo(
    () =>
      createInterview(httpInterviewApi(session), {
        newId: () => `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
      }),
    // One Interview per sign-in; token functions are read at call time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.status],
  );
  const state = useSyncExternalStore(iv.subscribe, iv.getState);
  // Set once the server has been asked whether an unfinished Interview exists (not when it could not be reached).
  const [opened, setOpened] = useState(false);
  useEffect(() => {
    setOpened(false);
    iv.open().then(() => setOpened(true)).catch(() => {});
  }, [iv]);
  return { ...state, opened, begin: iv.begin, send: iv.send, retry: iv.retry, chooseShortName: iv.chooseShortName, correct: iv.correct, confirm: iv.confirm };
}

/** Whether an unfinished Interview exists (null while unknown), for the card on Today. */
export function useUnfinishedInterview() {
  const session = useSession();
  const [unfinished, setUnfinished] = useState<boolean | null>(null);
  useFocusEffect(
    useCallback(() => {
      httpInterviewApi(session)
        .current()
        .then((v) => setUnfinished(v !== null))
        .catch(() => {});
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [session.status]),
  );
  return unfinished;
}
