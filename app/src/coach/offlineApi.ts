import type { Sync } from '../sync/engine';
import type { Storage } from '../sync/storage';
import type { CoachApi, StoredMessage } from './chat';

/**
 * The Coach thread from the phone: history is the cached messages plus what is still waiting in the outbox, and a
 * message goes through the outbox with its client ID. The Coach's reply arrives with the next pull, so `send` does
 * not stream: the message stays Saved until the pull shows the Coach has answered it.
 */
export function offlineCoachApi(deps: { storage: Storage; sync: Sync; now: () => string }): CoachApi {
  return {
    async history() {
      const cached = (await deps.storage.list<StoredMessage>('chat_message')).sort((a, b) => a.createdAt.localeCompare(b.createdAt));
      const known = new Set(cached.map((m) => m.clientId).filter(Boolean));
      const waiting = (await deps.sync.outbox())
        .filter((i) => i.kind === 'message' && !known.has(i.clientId))
        .map((i): StoredMessage => ({
          id: i.clientId, role: 'user', text: String(i.payload.text ?? ''), clientId: i.clientId, createdAt: i.queuedAt, replied: false,
        }));
      return [...cached, ...waiting];
    },
    async send(text, clientId) {
      // Resending a Saved message must not queue it twice: the same client ID is already waiting.
      if ((await deps.sync.outbox()).some((i) => i.clientId === clientId)) void deps.sync.sync();
      else await deps.sync.enqueue('message', { text }, clientId);
    },
  };
}
