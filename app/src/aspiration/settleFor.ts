import type { TFunction } from 'i18next';

/**
 * Settle-for, written once: what the user would settle for and by when ("86 kg by 15 Jul 2027"). Pictures saved
 * before the Interview asked for the "what" alone may end in their own "by mid July": the date shown replaces it.
 */
export function settleForLine(t: TFunction, what: string | null | undefined, when: string | null | undefined): string | undefined {
  const w = when ? what?.replace(/\s+by\s+[^,;]*$/i, '').trim() : what?.trim();
  if (w && when) return t('settleFor.whatBy', { what: w, date: when });
  return w || when || undefined;
}
