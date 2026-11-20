import * as SecureStore from 'expo-secure-store';
import { useSyncExternalStore } from 'react';
import { UnistylesRuntime } from 'react-native-unistyles';

/** Settings → Appearance. "system" follows the phone; the choice stays on this phone. */
export type Appearance = 'system' | 'light' | 'dark';
export const APPEARANCES: Appearance[] = ['system', 'light', 'dark'];

const KEY = 'appearance';
const listeners = new Set<() => void>();
let current: Appearance = read();

function read(): Appearance {
  try {
    const v = SecureStore.getItem(KEY);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
}

function apply(a: Appearance) {
  if (a === 'system') {
    UnistylesRuntime.setAdaptiveThemes(true);
  } else {
    UnistylesRuntime.setAdaptiveThemes(false);
    UnistylesRuntime.setTheme(a);
  }
}

/** Called once at start, before the first screen draws. */
export function applySavedAppearance() {
  apply(current);
}

export function setAppearance(a: Appearance) {
  current = a;
  apply(a);
  void SecureStore.setItemAsync(KEY, a).catch(() => {});
  listeners.forEach((fn) => fn());
}

export function useAppearance(): Appearance {
  return useSyncExternalStore(
    (fn) => {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    () => current,
  );
}
