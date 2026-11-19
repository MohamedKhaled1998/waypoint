import { fetch } from 'expo/fetch';
import type { AspirationProgress, ProgressView } from './progress';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The Progress API. A 401 renews the access token once and retries. */
export function httpProgressApi(auth: Auth) {
  async function get(path: string) {
    const go = () => fetch(`${baseUrl}${path}`, { headers: { Authorization: `Bearer ${auth.getAccessToken() ?? ''}` } });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Progress API ${res.status}`);
    return res.json();
  }
  return {
    all: async () => (await get('/progress')) as ProgressView,
    one: async (aspirationId: string) => (await get(`/progress/${aspirationId}`)) as AspirationProgress,
  };
}
