import type { CacheKind, CacheWrite, OutboxItem, OutboxKind, Storage } from './storage';

export type Change = { kind: CacheKind; id: string; deleted: boolean; data: unknown };
export type Pull = {
  cursor: number;
  more: boolean;
  changes: Change[];
  slots: { id: string; label: string; days: string; start: string; end: string }[];
};

/** What the server said to one outbox item. A thrown error means it could not be reached: try again later. */
export type SendResult = { kind: 'sent' } | { kind: 'rejected'; status: number; newer?: unknown };

export interface Remote {
  send(item: OutboxItem): Promise<SendResult>;
  pull(cursor: number): Promise<Pull>;
}

export type Rejection = { item: OutboxItem; status: number; newer?: unknown };

type Deps = {
  storage: Storage;
  remote: Remote;
  now: () => Date;
  newId: () => string;
  /** The cache changed (a pull landed or something was queued). */
  onChange?: () => void;
  /** The server turned an item down for good; it is out of the outbox. */
  onRejected?: (r: Rejection) => void;
};

/** What a pull page does to the cache. Slots come whole with every page. */
export function pullToWrite(pull: Pull): CacheWrite {
  const write: CacheWrite = { upserts: [], removes: [] };
  for (const c of pull.changes) {
    if (c.deleted) write.removes.push({ kind: c.kind, id: c.id });
    else write.upserts.push({ kind: c.kind, id: c.id, data: c.data });
  }
  for (const s of pull.slots) write.upserts.push({ kind: 'slot', id: s.id, data: s });
  return write;
}

/**
 * The outbox and the pull. Items go in the order they were queued, one at a time; an item that cannot be sent stays
 * at the front, so nothing overtakes it and nothing is sent twice under a new ID. When the outbox is empty the phone
 * pulls everything after its cursor.
 */
export function createSync(deps: Deps) {
  let running: Promise<'synced' | 'offline'> | null = null;
  let again = false;
  const rejections = new Map<string, Rejection>();

  async function drain(): Promise<boolean> {
    for (let items = await deps.storage.outbox(); items.length > 0; items = await deps.storage.outbox()) {
      const item = items[0]!;
      let result: SendResult;
      try {
        result = await deps.remote.send(item);
      } catch {
        return false;
      }
      await deps.storage.dequeue(item.clientId);
      if (result.kind === 'rejected') {
        const rejection = { item, status: result.status, newer: result.newer };
        rejections.set(item.clientId, rejection);
        // A stale approval shows the version that replaced it.
        const newer = result.newer as { id?: unknown } | undefined;
        const writes: CacheWrite = { upserts: [], removes: [] };
        if (item.kind === 'approve_plan' && typeof newer?.id === 'string') writes.upserts.push({ kind: 'plan_version', id: newer.id, data: newer });
        // The optimistic copy of what the user did may be wrong now: start the pull over from 0, which replaces the
        // whole cache with the server's copy (so rows deleted on the server go too).
        await deps.storage.apply(writes, 0);
        deps.onRejected?.(rejection);
      }
    }
    return true;
  }

  async function pullAll() {
    if ((await deps.storage.cursor()) === 0) return pullFresh();
    for (;;) {
      const pull = await deps.remote.pull(await deps.storage.cursor());
      await deps.storage.apply(pullToWrite(pull), pull.cursor);
      if (!pull.more) return;
    }
  }

  /** From the start: every page is gathered first, then the cache is replaced in one step, so a failure part way leaves it as it was. */
  async function pullFresh() {
    const write: CacheWrite = { replace: true, upserts: [], removes: [] };
    let cursor = 0;
    for (;;) {
      const pull = await deps.remote.pull(cursor);
      const page = pullToWrite(pull);
      // A later page can delete or change what an earlier one added.
      for (const r of page.removes) write.upserts = write.upserts.filter((u) => !(u.kind === r.kind && u.id === r.id));
      write.upserts.push(...page.upserts);
      cursor = pull.cursor;
      if (!pull.more) break;
    }
    await deps.storage.apply(write, cursor);
  }

  async function run(): Promise<'synced' | 'offline'> {
    do {
      again = false;
      try {
        if (!(await drain())) return 'offline';
        if (again) continue; // something was queued while draining: send it before pulling
        await pullAll();
      } catch {
        return 'offline';
      }
    } while (again);
    deps.onChange?.();
    return 'synced';
  }

  return {
    /** Drains the outbox, then pulls. One run at a time; a call during a run makes it go round again. */
    sync(): Promise<'synced' | 'offline'> {
      if (running) {
        again = true;
        return running;
      }
      running = run().finally(() => {
        running = null;
      });
      return running;
    },
    /** Queues the user's action and tries to send it. Resolves once it is stored; sending carries on behind. */
    async enqueue(kind: OutboxKind, payload: Record<string, unknown>, clientId = deps.newId(), write?: CacheWrite) {
      await deps.storage.enqueue({ clientId, kind, payload, queuedAt: deps.now().toISOString() });
      if (write) await deps.storage.apply(write);
      deps.onChange?.();
      void this.sync();
      return clientId;
    },
    outbox: () => deps.storage.outbox(),
    /** Why the server turned this item down, if it did (kept for this run of the app). */
    rejection: (clientId: string) => rejections.get(clientId),
  };
}

export type Sync = ReturnType<typeof createSync>;
