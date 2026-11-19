import type { ReminderGroup } from './arming';

/** What a reminder carries to the headless task: row ID only, no free text. */
export type ReminderData = { actionId: string };

export type ArmedReminder = {
  /** Same for every child in a group, so they stack into one grouped notification. */
  groupKey: string;
  at: Date;
  title: string;
  body: string | null;
  data: ReminderData;
};

/** The phone's notification scheduler, small so the arming rules can be tested without the native module. */
export interface Notifier {
  /** Drops every armed reminder. */
  cancelAll(): Promise<void>;
  schedule(reminder: ArmedReminder): Promise<void>;
  /** Clears one shown notification. */
  dismiss(notificationId: string): Promise<void>;
}

/** Re-arms from the cache: drops what was armed and arms the next moments, so a changed Action is never stale. */
export async function arm(notifier: Notifier, groups: ReminderGroup[]) {
  await notifier.cancelAll();
  for (const g of groups)
    for (const r of g.reminders)
      await notifier.schedule({ groupKey: g.key, at: r.at, title: r.title, body: r.text, data: { actionId: r.actionId } });
}
