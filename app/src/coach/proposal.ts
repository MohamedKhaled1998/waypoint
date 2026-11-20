import type { PlanSummary } from '../plan/review';

/**
 * Where a proposal card in the thread stands: still waiting for the user, the Plan now in force (approved), or past
 * (a later version replaced it, or it was never taken up).
 */
export type ProposalState = { kind: 'waiting' } | { kind: 'approved'; approvedOn: string | null } | { kind: 'past' };

export function proposalState(versionId: string, plans: Pick<PlanSummary, 'pendingVersionId' | 'currentVersionId' | 'currentApprovedAt'>[] | null | undefined): ProposalState {
  // Before the summaries load, the card offers Review as it always did.
  if (!plans) return { kind: 'waiting' };
  if (plans.some((p) => p.pendingVersionId === versionId)) return { kind: 'waiting' };
  const current = plans.find((p) => p.currentVersionId === versionId);
  if (current) return { kind: 'approved', approvedOn: current.currentApprovedAt ? localDay(current.currentApprovedAt) : null };
  return { kind: 'past' };
}

/** The server sends UTC; the card names the user's own day. */
function localDay(utc: string): string {
  const d = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(utc) ? utc : `${utc}Z`);
  if (Number.isNaN(d.getTime())) return utc.slice(0, 10);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}
