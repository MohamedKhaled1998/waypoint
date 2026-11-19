/** What the phone keeps for the server's rows, by kind. */
export type CacheKind = 'scheduled_action' | 'milestone' | 'chat_message' | 'plan_version' | 'slot' | 'pauses';

export type OutboxKind = 'check_in' | 'undo_check_in' | 'message' | 'approve_plan' | 'move_action' | 'pause' | 'resume';

/** One thing the user did, waiting to be sent. `clientId` is also what the server dedupes on. */
export type OutboxItem = { clientId: string; kind: OutboxKind; payload: Record<string, unknown>; queuedAt: string };

export type CacheWrite = {
  /** Drops every cached row first, in the same step: what is left is exactly what the server sent. */
  replace?: boolean;
  upserts: { kind: CacheKind; id: string; data: unknown }[];
  removes: { kind: CacheKind; id: string }[];
};

/**
 * The phone's store: a cache of what the server sent, the outbox, and the sync cursor. Small on purpose, so a web
 * client can bring its own. The phone never changes a Plan: the cache only takes what the server sent, plus the
 * user's own Check-ins and moves until the server confirms them.
 */
export interface Storage {
  list<T>(kind: CacheKind): Promise<T[]>;
  /** Applies the writes and, when given, the new cursor, as one step. */
  apply(write: CacheWrite, cursor?: number): Promise<void>;
  cursor(): Promise<number>;
  /** Adds to the end of the outbox. */
  enqueue(item: OutboxItem): Promise<void>;
  /** In the order they were queued. */
  outbox(): Promise<OutboxItem[]>;
  dequeue(clientId: string): Promise<void>;
  /** Sign-out: the cache, the outbox and the cursor all go. */
  wipe(): Promise<void>;
}

/** In memory: used by the tests and as the reference for the contract. */
export function createMemoryStorage(): Storage {
  let cache = new Map<string, unknown>();
  let queue: OutboxItem[] = [];
  let cursor = 0;
  const key = (kind: CacheKind, id: string) => `${kind}:${id}`;
  return {
    async list<T>(kind: CacheKind) {
      return [...cache].filter(([k]) => k.startsWith(`${kind}:`)).map(([, v]) => v as T);
    },
    async apply(write, next) {
      if (write.replace) cache = new Map();
      for (const r of write.removes) cache.delete(key(r.kind, r.id));
      for (const u of write.upserts) cache.set(key(u.kind, u.id), u.data);
      if (next !== undefined) cursor = next;
    },
    async cursor() {
      return cursor;
    },
    async enqueue(item) {
      if (!queue.some((i) => i.clientId === item.clientId)) queue.push(item);
    },
    async outbox() {
      return [...queue];
    },
    async dequeue(clientId) {
      queue = queue.filter((i) => i.clientId !== clientId);
    },
    async wipe() {
      cache = new Map();
      queue = [];
      cursor = 0;
    },
  };
}
