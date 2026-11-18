export type PermissionStep = 'notifications' | 'exactAlarms';

export type PermissionPlatform = {
  /** Whether each permission is granted already. */
  granted(step: PermissionStep): Promise<boolean>;
  /** Shows the OS prompt or settings screen for the step. */
  request(step: PermissionStep): Promise<void>;
  /** Tells the user why it is needed and what happens without it; true when they agree to go on. */
  explain(step: PermissionStep): Promise<boolean>;
};

/**
 * Right after the first Plan is approved: notifications, then exact alarms, one at a time. Each step explains itself
 * first; the next one is not raised until this one is settled. Declining exact alarms is fine: reminders still arrive.
 */
/** Remembers on the phone that the one-time prompts were raised. */
export type AskedFlag = { get(): Promise<boolean>; set(): Promise<void> };

/**
 * Asks only after the first Plan's approval: later approvals never re-ask what the user declined. The flag is set
 * before asking, so a second approval racing the first cannot raise the prompts twice.
 */
export async function askOnceAfterFirstPlan(p: PermissionPlatform, flag: AskedFlag): Promise<PermissionStep[]> {
  if (await flag.get()) return [];
  await flag.set();
  return askForReminderPermissions(p);
}

export async function askForReminderPermissions(p: PermissionPlatform): Promise<PermissionStep[]> {
  const asked: PermissionStep[] = [];
  for (const step of ['notifications', 'exactAlarms'] as const) {
    if (await p.granted(step)) continue;
    asked.push(step);
    if (await p.explain(step)) await p.request(step);
    // Without notifications there is nothing to time: exact alarms are not worth asking about.
    if (step === 'notifications' && !(await p.granted('notifications'))) break;
  }
  return asked;
}
