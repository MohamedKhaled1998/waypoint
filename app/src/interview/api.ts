import { fetch } from 'expo/fetch';
import type { InterviewApi, InterviewView } from './interview';
import { createSseParser, type SseEvent } from '../coach/sse';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The Interview's HTTP API. A 401 renews the access token once and retries. */
export function httpInterviewApi(auth: Auth): InterviewApi {
  async function call(path: string, init: { method: string; body?: string }, okStatuses: number[] = []) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        ...init,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok && !okStatuses.includes(res.status)) throw new Error(`Interview API ${res.status}`);
    return res;
  }

  async function stream(path: string, body: unknown, onEvent: (e: SseEvent) => void) {
    const res = await call(path, { method: 'POST', body: JSON.stringify(body) });
    const parser = createSseParser();
    const decoder = new TextDecoder();
    const reader = res.body!.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      parser.feed(decoder.decode(value, { stream: true })).forEach(onEvent);
    }
  }

  return {
    async current() {
      const res = await call('/interviews/current', { method: 'GET' }, [404]);
      return res.status === 404 ? null : ((await res.json()) as InterviewView);
    },
    async start(ownWords) {
      return (await (await call('/interviews', { method: 'POST', body: JSON.stringify({ ownWords }) })).json()) as InterviewView;
    },
    resume: (id, onEvent) => stream(`/interviews/${id}/resume`, {}, onEvent),
    say: (id, text, clientId, onEvent) => stream(`/interviews/${id}/messages`, { text, clientId }, onEvent),
    async confirm(id, shortName) {
      return await (await call(`/interviews/${id}/confirm`, { method: 'POST', body: JSON.stringify({ shortName }) })).json();
    },
  };
}
