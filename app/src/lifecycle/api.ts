import { fetch } from 'expo/fetch';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The lifecycle calls. A 401 renews the access token once and retries. */
export function httpLifecycleApi(auth: Auth) {
  async function call(path: string, method: 'POST' | 'DELETE', body?: unknown) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        method,
        headers: { Authorization: `Bearer ${auth.getAccessToken() ?? ''}`, 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Lifecycle API ${res.status}`);
  }
  return {
    moveToMaintenance: (id: string) => call(`/aspirations/${id}/maintenance`, 'POST'),
    markReached: (id: string, keepUp: boolean) => call(`/aspirations/${id}/reached`, 'POST', { keepUp }),
    newChapter: (id: string) => call(`/aspirations/${id}/new-chapter`, 'POST'),
    remove: (id: string) => call(`/aspirations/${id}`, 'DELETE'),
  };
}
