import type { Sync } from '../sync/engine';
import type { Storage } from '../sync/storage';
import type { CoachApi, StoredMessage } from './chat';

/**
 * The Coach thread from the phone: history is the cached messages plus what is still waiting in the outbox, and a
 * message goes through the outbox with its client ID. The Coach's reply arrives with the pull, so `send` does not
 * stream: it resolves once the pull shows the message answered, and fails (the Coach is late) otherwise.
 */
export function offlineCoachApi(deps: { storage: Storage; sync: Sync; now: () => string }): CoachApi {
  return {
    async history() {
      const cached = (await deps.storage.list<StoredMessage>('chat_message')).sort((a, b) => a.createdAt.localeCompare(b.createdAt));
      const known = new Set(cached.map((m) => m.clientId).filter(Boolean));
      const waiting = (await deps.sync.outbox())
        .filter((i) => i.kind === 'message' && !known.has(i.clientId))
        .map((i): StoredMessage => ({
          id: i.clientId, role: 'user', text: String(i.payload.text ?? ''), aspirationId: (i.payload.aspirationId as string | undefined) ?? null, clientId: i.clientId, createdAt: i.queuedAt, replied: false,
        }));
      return [...cached, ...waiting];
    },
    async send(text, clientId, onEvent, aspirationId, intent) {
      // Resending a Saved message must not queue it twice: the same client ID is already waiting.
      if (!(await deps.sync.outbox()).some((i) => i.clientId === clientId))
        await deps.sync.enqueue('message', { text, ...(aspirationId ? { aspirationId } : {}), ...(intent ? { intent } : {}) }, clientId);
      // The server answers while the message is sent, and the pull after it brings the reply: the chat shows the
      // Coach typing until then. Unreachable, or kept without a reply, the Coach is late.
      // A run already going when the message was queued (the one at app start, say) may fail before reaching it: one
      // fresh run decides.
      if ((await deps.sync.sync()) === 'offline' && (await deps.sync.sync()) === 'offline') throw new Error('offline');
      const mine = (await deps.storage.list<StoredMessage>('chat_message')).find((m) => m.clientId === clientId);
      if (!mine?.replied) throw new Error('late');
      onEvent({ name: 'done', data: {} });
    },
  };
}
