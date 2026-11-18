import { fetch } from 'expo/fetch';
import type { CoachApi, StoredMessage } from './chat';
import { createSseParser } from './sse';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The Coach's HTTP API. A 401 renews the access token once and retries. */
export function httpCoachApi(auth: Auth): CoachApi {
  async function call(path: string, init: { method: string; body?: string }) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        ...init,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Coach API ${res.status}`);
    return res;
  }

  return {
    async history() {
      return (await (await call('/coach/messages', { method: 'GET' })).json()) as StoredMessage[];
    },
    async send(text, clientId, onEvent) {
      const res = await call('/coach/messages', { method: 'POST', body: JSON.stringify({ text, clientId }) });
      const parser = createSseParser();
      const decoder = new TextDecoder();
      const reader = res.body!.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        parser.feed(decoder.decode(value, { stream: true })).forEach(onEvent);
      }
    },
  };
}
