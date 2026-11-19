/** An Aspiration's lifecycle as the overflow menu offers it. The server decides what each choice does; the phone only asks. */

export type LifecycleStatus = 'active' | 'maintenance' | 'closed';

/** Weeks kept up of the last few, for a maintenance Aspiration. */
export type Consistency = { weeksKeptUp: number; ofWeeks: number };

export type MenuItem =
  | 'pause'
  | 'resume'
  | 'move_to_maintenance'
  | 'new_chapter'
  | 'mark_reached'
  | 'delete';

/** What a menu item does when it is picked: act at once, or ask first (with the choices the confirm shows). */
export type Confirm = {
  /** i18n key prefix: `<prefix>.title`, `<prefix>.body` and one `<prefix>.<choice>` per choice. */
  prefix: string;
  choices: ('confirm' | 'keepUp' | 'close' | 'pauseInstead')[];
  destructive: boolean;
};

/** The overflow menu for an Aspiration in this state, in the order it is shown. Delete is always last. */
export function menuItems(status: LifecycleStatus, paused: boolean): MenuItem[] {
  if (status === 'closed') return ['delete'];
  if (paused) return ['resume', 'delete'];
  if (status === 'maintenance') return ['pause', 'new_chapter', 'mark_reached', 'delete'];
  return ['pause', 'move_to_maintenance', 'mark_reached', 'delete'];
}

/** Which items ask first. Pause and Resume never do; a new chapter is just a short Interview. */
export function confirmFor(item: MenuItem, paused = false): Confirm | null {
  switch (item) {
    case 'move_to_maintenance':
      return { prefix: 'lifecycle.maintenance', choices: ['confirm'], destructive: false };
    case 'mark_reached':
      return { prefix: 'lifecycle.reached', choices: ['keepUp', 'close'], destructive: false };
    case 'delete':
      // Can't be undone, so the confirm suggests Pause instead.
      // A paused (or closed) one has nothing left to pause, so the suggestion is dropped.
      return { prefix: 'lifecycle.delete', choices: paused ? ['confirm'] : ['pauseInstead', 'confirm'], destructive: true };
    default:
      return null;
  }
}

/** "5 of the last 8 weeks kept up": the counts, never a percentage. */
export function consistencyLine(c: Consistency, t: (key: 'lifecycle.consistency', values: { kept: number; of: number }) => string): string {
  return t('lifecycle.consistency', { kept: c.weeksKeptUp, of: c.ofWeeks });
}

/** A call to the server that the menu waits on. */
export type Work = () => Promise<void>;
