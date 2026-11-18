import type { Sync } from '../sync/engine';
import type { ApproveResult, PlanApi, PlanVersionView } from './review';

/**
 * Plan approval through the outbox. The version is read from the server, but approving is queued with a client ID and
 * sent in order. If the server turned it down because the version was replaced, the newer one comes back (and the
 * sync has already put it in the cache); if the server cannot be reached, the approval stays queued.
 */
export function offlinePlanApi(deps: { sync: Sync; api: Pick<PlanApi, 'get'> }): PlanApi {
  return {
    get: (id) => deps.api.get(id),
    async approve(id): Promise<ApproveResult> {
      const clientId = await deps.sync.enqueue('approve_plan', { versionId: id });
      await deps.sync.sync();
      if ((await deps.sync.outbox()).some((i) => i.clientId === clientId)) return { kind: 'queued' };
      const rejection = deps.sync.rejection(clientId);
      if (rejection) {
        const newer = (rejection.newer as PlanVersionView | undefined) ?? null;
        return { kind: 'stale', newer, reason: newer ? 'replaced' : 'window_started' };
      }
      return { kind: 'approved', version: await deps.api.get(id) };
    },
  };
}
