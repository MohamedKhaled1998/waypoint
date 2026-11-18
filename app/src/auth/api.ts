import type { ApiAuth, TokenPair } from './session';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

async function post(path: string, body: unknown) {
  return fetch(`${baseUrl}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export const httpAuth: ApiAuth = {
  async signInWithGoogle(idToken) {
    const res = await post('/auth/google', { idToken });
    if (!res.ok) throw new Error(`sign-in rejected (${res.status})`);
    return (await res.json()) as TokenPair;
  },
  async refresh(refreshToken) {
    const res = await post('/auth/refresh', { refreshToken });
    if (res.status === 401) return null;
    if (!res.ok) throw new Error(`refresh failed (${res.status})`);
    return (await res.json()) as TokenPair;
  },
  async signOut(refreshToken) {
    await post('/auth/sign-out', { refreshToken });
  },
};
