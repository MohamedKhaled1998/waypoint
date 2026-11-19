import { fetch } from 'expo/fetch';
import type { MeasureView, Reading } from './measure';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The Measure API. A 401 renews the access token once and retries. */
export function httpMeasureApi(auth: Auth) {
  async function call(path: string, init?: { method: string; body?: string }) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        ...init,
        headers: { Authorization: `Bearer ${auth.getAccessToken() ?? ''}`, 'Content-Type': 'application/json' },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Measure API ${res.status}`);
    return res.json();
  }
  return {
    get: async (aspirationId: string) => (await call(`/aspirations/${aspirationId}/measure/`)) as MeasureView,
    addReading: async (aspirationId: string, value: number, clientId: string) =>
      (await call(`/aspirations/${aspirationId}/measure/readings`, { method: 'POST', body: JSON.stringify({ value, clientId }) })) as Reading,
  };
}
