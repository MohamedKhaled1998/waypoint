import { useSyncExternalStore } from 'react';
import { keystoreStorage } from '../auth/secureStorage';
import { askedFlag, phonePermissions } from '../reminders/native';
import { askOnceAfterFirstPlan } from '../reminders/permissions';
import { rearmReminders } from '../reminders/rearm';
import { createFirstRun } from './firstRun';

/** The one first run for this phone, kept in the Keystore-backed store. */
export const firstRun = createFirstRun(keystoreStorage);
void firstRun.load().catch(() => {});

export function useFirstRun() {
  return useSyncExternalStore(firstRun.subscribe, firstRun.getState);
}

/** The permissions step: asked once, reminders armed, then the tour. Runs after approval and again if the app was killed there. */
export async function settlePermissions(): Promise<void> {
  try {
    await askOnceAfterFirstPlan(phonePermissions, askedFlag).catch(() => {});
    await rearmReminders();
  } catch {
    console.warn('reminders: arming failed');
  } finally {
    await firstRun.permissionsSettled();
  }
}
