/**
 * The one way the app writes a calendar date: "Sun 22 Nov" (in the user's locale), with the year only when it
 * isn't this year. Takes the server's `yyyy-MM-dd` (a calendar day, no time zone).
 */
export function formatDay(iso: string, opts: { locale?: string; today?: Date; weekday?: boolean } = {}): string {
  const date = parseDay(iso);
  if (!date) return iso;
  const today = opts.today ?? new Date();
  const withYear = date.getFullYear() !== today.getFullYear();
  return new Intl.DateTimeFormat(opts.locale, {
    weekday: opts.weekday === false ? undefined : 'short',
    day: 'numeric',
    month: 'short',
    year: withYear ? 'numeric' : undefined,
  }).format(date);
}

/** Noon local time, so no time zone can push the day over midnight. */
export function parseDay(iso: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!m) return null;
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), 12);
}
