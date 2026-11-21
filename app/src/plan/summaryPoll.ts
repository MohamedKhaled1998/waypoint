import type { PlanSummary } from './review';

const EVERY_MS = 10_000;

/**
 * Fetches the Plan summaries now and, while a Plan is still drafting, again every 10 s, so Today notices the proposal
 * without a push (in first run, notifications aren't allowed yet). Returns a stop function.
 */
export function pollPlanSummaries(api: { summaries(): Promise<PlanSummary[]> }, onPlans: (plans: PlanSummary[]) => void): () => void {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let stopped = false;
  let drafting = false;
  const check = async () => {
    try {
      const plans = await api.summaries();
      if (stopped) return;
      onPlans(plans);
      drafting = plans.some((p) => p.drafting);
    } catch {
      // Offline for a moment: keep checking if a Plan was drafting.
    }
    if (drafting && !stopped) timer = setTimeout(() => void check(), EVERY_MS);
  };
  void check();
  return () => {
    stopped = true;
    if (timer) clearTimeout(timer);
  };
}
