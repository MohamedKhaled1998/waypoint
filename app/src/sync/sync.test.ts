import { createSync, type Pull, type Remote, type SendResult } from './engine';
import { createMemoryStorage, type OutboxItem } from './storage';

const page = (over: Partial<Pull> = {}): Pull => ({ cursor: 0, more: false, changes: [], slots: [], ...over });
const queued = (clientId: string, kind: OutboxItem['kind'] = 'check_in'): OutboxItem => ({ clientId, kind, payload: {}, queuedAt: '' });

function setup(opts: { send?: (item: OutboxItem) => Promise<SendResult>; pulls?: Pull[] } = {}) {
  const storage = createMemoryStorage();
  const sent: string[] = [];
  const pulledFrom: number[] = [];
  const pulls = [...(opts.pulls ?? [])];
  const remote: Remote = {
    async send(item) {
      const result = opts.send ? await opts.send(item) : ({ kind: 'sent' } as const);
      sent.push(item.clientId);
      return result;
    },
    async pull(cursor) {
      pulledFrom.push(cursor);
      return pulls.shift() ?? page({ cursor });
    },
  };
  let n = 0;
  const rejected: unknown[] = [];
  const sync = createSync({ storage, remote, now: () => new Date('2026-01-05T09:00:00Z'), newId: () => `c-${++n}`, onRejected: (r) => rejected.push(r) });
  return { storage, sync, sent, pulledFrom, rejected };
}

describe('the outbox', () => {
  it('sends items in the order they were queued', async () => {
    const { sync, sent, storage } = setup();
    await storage.enqueue(queued('a'));
    await storage.enqueue(queued('b', 'message'));
    await storage.enqueue(queued('c', 'move_action'));
    expect(await sync.sync()).toBe('synced');
    expect(sent).toEqual(['a', 'b', 'c']);
    expect(await storage.outbox()).toEqual([]);
  });

  it('gives each item a client ID and keeps what it was sent with', async () => {
    const { sync, storage } = setup({ send: () => Promise.reject(new Error('offline')) });
    const id = await sync.enqueue('check_in', { state: 'done' });
    expect(id).toBe('c-1');
    expect(await storage.outbox()).toEqual([{ clientId: 'c-1', kind: 'check_in', payload: { state: 'done' }, queuedAt: '2026-01-05T09:00:00.000Z' }]);
  });

  it('retries a failed send without reordering or duplicating', async () => {
    let online = false;
    const attempts: string[] = [];
    const { sync, sent, storage } = setup({
      send: async (item) => {
        attempts.push(item.clientId);
        if (!online) throw new Error('offline');
        return { kind: 'sent' };
      },
    });
    for (const id of ['a', 'b', 'c']) await storage.enqueue(queued(id));

    expect(await sync.sync()).toBe('offline');
    expect(attempts).toEqual(['a']); // b and c do not overtake a
    expect((await storage.outbox()).map((i) => i.clientId)).toEqual(['a', 'b', 'c']);

    online = true;
    expect(await sync.sync()).toBe('synced');
    expect(sent).toEqual(['a', 'b', 'c']);
    expect(attempts).toEqual(['a', 'a', 'b', 'c']);
  });

  it('keeps going after a rejection, drops the item and tells the caller why', async () => {
    const { sync, sent, storage, rejected } = setup({
      send: async (item) => (item.clientId === 'a' ? { kind: 'rejected', status: 409, newer: { id: 'v2' } } : { kind: 'sent' }),
    });
    await storage.apply({ upserts: [{ kind: 'scheduled_action', id: 'x', data: { optimistic: true } }], removes: [] }, 7);
    await storage.enqueue(queued('a', 'approve_plan'));
    await storage.enqueue(queued('b'));
    await sync.sync();
    expect(sent).toEqual(['a', 'b']);
    expect(rejected).toEqual([{ item: expect.objectContaining({ clientId: 'a' }), status: 409, newer: { id: 'v2' } }]);
    expect(await storage.outbox()).toEqual([]);
    expect(await storage.cursor()).toBe(0); // the pull starts over, so the server's copy replaces the optimistic one
  });

  it('removes rows the server deleted since the last pull, and keeps a failed re-pull from emptying the cache', async () => {
    let online = true;
    const storage = createMemoryStorage();
    await storage.apply({ upserts: [
      { kind: 'scheduled_action', id: 'gone', data: { id: 'gone' } },
      { kind: 'scheduled_action', id: 'kept', data: { id: 'kept', optimistic: true } },
    ], removes: [] }, 7);
    await storage.enqueue(queued('a'));
    const sync = createSync({
      storage, now: () => new Date(), newId: () => 'x',
      remote: {
        send: async () => ({ kind: 'rejected', status: 409 }),
        pull: async (cursor) => {
          if (!online) throw new Error('down');
          return page({ cursor: 9, changes: [{ kind: 'scheduled_action', id: 'kept', deleted: false, data: { id: 'kept' } }] });
        },
      },
    });
    online = false;
    expect(await sync.sync()).toBe('offline'); // rejected, but the re-pull could not run
    expect(await storage.list('scheduled_action')).toHaveLength(2);
    expect(await storage.cursor()).toBe(0);
    online = true;
    expect(await sync.sync()).toBe('synced');
    expect(await storage.list('scheduled_action')).toEqual([{ id: 'kept' }]);
    expect(await storage.cursor()).toBe(9);
  });

  it('sends an item queued while it is draining before it pulls', async () => {
    const { sync, sent, pulledFrom } = setup();
    await sync.enqueue('check_in', {}, 'a');
    await sync.enqueue('check_in', {}, 'b');
    await sync.sync();
    expect(sent).toEqual(['a', 'b']);
    expect(pulledFrom.length).toBeGreaterThan(0);
  });
});

describe('the pull', () => {
  it('pulls from the cursor once the outbox has drained, and not before', async () => {
    const order: string[] = [];
    const storage = createMemoryStorage();
    await storage.apply({ upserts: [], removes: [] }, 41);
    await storage.enqueue(queued('a'));
    const sync = createSync({
      storage, now: () => new Date(), newId: () => 'x',
      remote: {
        send: async (i) => (order.push(`send ${i.clientId}`), { kind: 'sent' }),
        pull: async (cursor) => (order.push(`pull ${cursor}`), page({ cursor: 50 })),
      },
    });
    await sync.sync();
    expect(order).toEqual(['send a', 'pull 41']);
    expect(await storage.cursor()).toBe(50);
  });

  it('does not pull while an item is still waiting', async () => {
    const { sync, pulledFrom, storage } = setup({ send: () => Promise.reject(new Error('offline')) });
    await storage.enqueue(queued('a'));
    expect(await sync.sync()).toBe('offline');
    expect(pulledFrom).toEqual([]);
  });

  it('applies changes and deletions to the cache and moves the cursor, page after page', async () => {
    const { sync, storage, pulledFrom } = setup({
      pulls: [
        page({
          cursor: 3, more: true,
          changes: [
            { kind: 'scheduled_action', id: 'a1', deleted: false, data: { id: 'a1', title: 'Run' } },
            { kind: 'chat_message', id: 'm1', deleted: false, data: { id: 'm1' } },
          ],
          slots: [{ id: 's1', label: 'mornings', days: 'mon', start: '07:00', end: '07:45' }],
        }),
        page({ cursor: 5, changes: [{ kind: 'chat_message', id: 'm1', deleted: true, data: null }] }),
      ],
    });
    await sync.sync();
    expect(pulledFrom).toEqual([0, 3]);
    expect(await storage.cursor()).toBe(5);
    expect(await storage.list('scheduled_action')).toEqual([{ id: 'a1', title: 'Run' }]);
    expect(await storage.list('chat_message')).toEqual([]);
    expect(await storage.list('slot')).toHaveLength(1);
  });

  it('leaves the cache and cursor alone when the server cannot be reached', async () => {
    const storage = createMemoryStorage();
    await storage.apply({ upserts: [{ kind: 'scheduled_action', id: 'a1', data: { id: 'a1' } }], removes: [] }, 4);
    const sync = createSync({
      storage, now: () => new Date(), newId: () => 'x',
      remote: { send: async () => ({ kind: 'sent' }), pull: () => Promise.reject(new Error('down')) },
    });
    expect(await sync.sync()).toBe('offline');
    expect(await storage.list('scheduled_action')).toHaveLength(1);
    expect(await storage.cursor()).toBe(4);
  });
});

describe('sign-out', () => {
  it('wipes the cache, the outbox and the cursor', async () => {
    const { storage } = setup();
    await storage.apply({ upserts: [{ kind: 'scheduled_action', id: 'a1', data: {} }, { kind: 'slot', id: 's', data: {} }], removes: [] }, 9);
    await storage.enqueue(queued('a'));
    await storage.wipe();
    expect(await storage.list('scheduled_action')).toEqual([]);
    expect(await storage.list('slot')).toEqual([]);
    expect(await storage.outbox()).toEqual([]);
    expect(await storage.cursor()).toBe(0);
  });
});
