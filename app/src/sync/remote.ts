import { fetch } from 'expo/fetch';
import type { Pull, Remote, SendResult } from './engine';
import type { OutboxItem } from './storage';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

/** The server end of the outbox and the pull. A 401 renews the access token once and retries. */
export function httpRemote(auth: Auth): Remote {
  async function call(path: string, method: string, body?: unknown) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        method,
        body: body === undefined ? undefined : JSON.stringify(body),
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    return res;
  }

  /** 2xx: sent. A 4xx other than 401, 408 and 429 is the server saying no for good. Anything else: try again later. */
  async function outcome(res: Awaited<ReturnType<typeof call>>): Promise<SendResult> {
    if (res.ok) return { kind: 'sent' };
    if (res.status >= 400 && res.status < 500 && ![401, 408, 429].includes(res.status)) {
      const body = res.status === 409 ? ((await res.json().catch(() => null)) as { newer?: unknown } | null) : null;
      return { kind: 'rejected', status: res.status, newer: body?.newer ?? undefined };
    }
    throw new Error(`Sync ${res.status}`);
  }

  const p = (i: OutboxItem) => i.payload as Record<string, any>;

  return {
    async send(item) {
      switch (item.kind) {
        case 'check_in':
          return outcome(await call('/check-ins', 'PUT', { ...item.payload, clientId: item.clientId }));
        case 'undo_check_in':
          return outcome(await call(`/scheduled-actions/${p(item).scheduledActionId}/check-in`, 'DELETE'));
        case 'move_action':
          return outcome(await call(`/scheduled-actions/${p(item).scheduledActionId}/move`, 'PUT', { clientId: item.clientId, date: p(item).date, slotId: p(item).slotId }));
        case 'pause':
          return outcome(await call('/pauses', 'POST', { ...item.payload, clientId: item.clientId }));
        case 'resume':
          return outcome(await call(`/pauses/by-client/${encodeURIComponent(p(item).pauseClientId)}/resume`, 'POST'));
        case 'habit_reminder':
          return outcome(await call(`/habits/${p(item).habitId}/reminder`, 'PUT', { on: p(item).on, fromOffer: p(item).fromOffer }));
        case 'approve_plan':
          return outcome(await call(`/plan-versions/${p(item).versionId}/approve`, 'POST'));
        case 'message': {
          const res = await call('/coach/messages', 'POST', { text: p(item).text, clientId: item.clientId, aspirationId: p(item).aspirationId });
          // The reply streams back; the pull brings it in, so the stream is read to its end and dropped.
          if (res.ok) await res.text();
          return outcome(res);
        }
      }
    },
    async pull(cursor) {
      const res = await call(`/sync?cursor=${cursor}`, 'GET');
      if (!res.ok) throw new Error(`Sync ${res.status}`);
      return (await res.json()) as Pull;
    },
  };
}
