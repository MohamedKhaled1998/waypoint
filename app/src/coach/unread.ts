import { useSyncExternalStore } from 'react';

/** When the Coach thread was last on screen. In memory: a restart counts the thread as unread again. */
let lastReadAt: string | null = null;
const listeners = new Set<() => void>();

export function markThreadRead(at: string) {
  lastReadAt = at;
  listeners.forEach((fn) => fn());
}

export function useLastReadAt() {
  return useSyncExternalStore(
    (fn) => {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    () => lastReadAt,
  );
}
