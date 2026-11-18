import { fetch } from 'expo/fetch';
import type { CheckInApi } from './checkin';
import type { CheckInRequest, CheckInView, ScheduledActionView } from './today';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** Today's HTTP API. A 401 renews the access token once and retries. */
export function httpTodayApi(auth: Auth): CheckInApi & { actions(): Promise<ScheduledActionView[]> } {
  async function call(path: string, method: string, body?: unknown) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        method,
        body: body === undefined ? undefined : JSON.stringify(body),
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Today API ${res.status}`);
    return res;
  }
  return {
    async actions() {
      return (await (await call('/scheduled-actions', 'GET')).json()) as ScheduledActionView[];
    },
    async put(req: CheckInRequest) {
      return (await (await call('/check-ins', 'PUT', req)).json()) as CheckInView;
    },
    async undo(id) {
      await call(`/scheduled-actions/${id}/check-in`, 'DELETE');
    },
  };
}
