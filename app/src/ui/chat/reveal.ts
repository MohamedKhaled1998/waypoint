/**
 * How many characters of a streaming reply to show next. Steady (about 3 characters a tick) while the stream
 * keeps up, faster when a burst arrives, so the reveal never trails the stream by more than a moment.
 */
export function nextReveal(shown: number, target: number, perTick = 3, maxLagTicks = 6): number {
  if (shown >= target) return target;
  const behind = target - shown;
  return Math.min(target, shown + Math.max(perTick, Math.ceil(behind / maxLagTicks)));
}
