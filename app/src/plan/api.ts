import { fetch } from 'expo/fetch';
import type { ApproveResult, PlanApi, PlanSummary, PlanVersionView } from './review';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The Plan's HTTP API. A 401 renews the access token once and retries. */
export function httpPlanApi(auth: Auth): PlanApi & { summaries(): Promise<PlanSummary[]>; retryDraft(aspirationId: string): Promise<void> } {
  async function call(path: string, method: string) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        method,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    return res;
  }

  return {
    async summaries() {
      const res = await call('/plans', 'GET');
      if (!res.ok) throw new Error(`Plan API ${res.status}`);
      return (await res.json()) as PlanSummary[];
    },
    async retryDraft(aspirationId) {
      const res = await call(`/aspirations/${aspirationId}/plan-retry`, 'POST');
      if (!res.ok) throw new Error(`Plan API ${res.status}`);
    },
    async get(id) {
      const res = await call(`/plan-versions/${id}`, 'GET');
      if (!res.ok) throw new Error(`Plan API ${res.status}`);
      return (await res.json()) as PlanVersionView;
    },
    async approve(id): Promise<ApproveResult> {
      const res = await call(`/plan-versions/${id}/approve`, 'POST');
      if (res.status === 409) {
        const body = (await res.json()) as { newer: PlanVersionView | null; reason?: 'replaced' | 'window_started' };
        return { kind: 'stale', newer: body.newer, reason: body.reason };
      }
      if (!res.ok) throw new Error(`Plan API ${res.status}`);
      return { kind: 'approved', version: (await res.json()) as PlanVersionView };
    },
  };
}
