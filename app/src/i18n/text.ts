/** A fragment shown on its own line starts with a capital. */
export function upperFirst(s: string): string {
  return s ? s.charAt(0).toLocaleUpperCase() + s.slice(1) : s;
}

/**
 * The Coach's words placed mid-sentence ("Reached when {{line}}") start lowercase, unless the first word is "I" or
 * an acronym ("GP", "5K").
 */
export function lowerFirst(s: string): string {
  const first = /^\S+/.exec(s)?.[0] ?? '';
  if (!first || first === 'I' || /^I['’]/.test(first) || (first.length > 1 && first === first.toLocaleUpperCase())) return s;
  return s.charAt(0).toLocaleLowerCase() + s.slice(1);
}
