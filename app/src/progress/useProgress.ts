import { useFocusEffect } from 'expo-router';
import { useCallback, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpProgressApi } from './api';
import type { AspirationProgress, ProgressView } from './progress';

/** Loads whenever the screen comes into focus, so a Check-in made on Today shows here at once. */
function useLoaded<T>(load: (api: ReturnType<typeof httpProgressApi>) => Promise<T>) {
  const session = useSession();
  const [value, setValue] = useState<T | null>(null);
  const [failed, setFailed] = useState(false);
  useFocusEffect(
    useCallback(() => {
      load(httpProgressApi(session))
        .then((v) => {
          setValue(v);
          setFailed(false);
        })
        .catch(() => setFailed(true));
      // Token functions are read at call time.
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [session.status]),
  );
  return { value, failed };
}

export const useProgress = () => useLoaded<ProgressView>((api) => api.all());
export const useAspirationProgress = (id: string) => useLoaded<AspirationProgress>((api) => api.one(id));
