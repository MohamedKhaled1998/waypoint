import { createSync } from './engine';
import { httpRemote } from './remote';
import { createSqliteStorage } from './sqliteStorage';

/** The one store on the phone. Sign-out wipes it. */
export const phoneStorage = createSqliteStorage();

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The phone's sync, with a way for screens to hear that the cache changed. */
export function createPhoneSync(auth: Auth) {
  const listeners = new Set<() => void>();
  const sync = createSync({
    storage: phoneStorage,
    remote: httpRemote(auth),
    now: () => new Date(),
    newId: () => `c-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`,
    onChange: () => listeners.forEach((fn) => fn()),
  });
  return {
    sync,
    subscribe(fn: () => void) {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
  };
}

export type PhoneSync = ReturnType<typeof createPhoneSync>;
