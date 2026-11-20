import { offlineCoachApi } from '../coach/offlineApi';
import { createCoachChat } from '../coach/chat';
import { offlinePlanApi } from '../plan/offlineApi';
import type { PlanVersionView } from '../plan/review';
import { createSync, type Pull, type Remote, type SendResult } from './engine';
import { createMemoryStorage } from './storage';

const empty = (over: Partial<Pull> = {}): Pull => ({ cursor: 0, more: false, changes: [], slots: [], ...over });

function phone(opts: { send?: (kind: string, clientId: string) => SendResult } = {}) {
  const storage = createMemoryStorage();
  const state = { online: false, sent: [] as string[], pulls: [] as Pull[] };
  const remote: Remote = {
    async send(item) {
      if (!state.online) throw new Error('offline');
      state.sent.push(`${item.kind}:${item.clientId}`);
      return opts.send?.(item.kind, item.clientId) ?? { kind: 'sent' };
    },
    async pull(cursor) {
      if (!state.online) throw new Error('offline');
      return state.pulls.shift() ?? empty({ cursor });
    },
  };
  let n = 0;
  const sync = createSync({ storage, remote, now: () => new Date('2026-01-05T09:00:00Z'), newId: () => `c-${++n}` });
  return { storage, sync, state };
}

describe('messages to the Coach', () => {
  it('queued offline, they show at once and send in order on reconnect, once each', async () => {
    const { storage, sync, state } = phone();
    const api = offlineCoachApi({ storage, sync, now: () => '2026-01-05T09:00:00Z' });
    let ids = 0;
    const chat = createCoachChat(api, { newId: () => `m${++ids}`, now: () => '2026-01-05T09:00:00Z', retryDelaysMs: [1_000_000] });

    await chat.send('first');
    await chat.send('second');
    expect(chat.state.messages.map((m) => [m.text, m.status])).toEqual([['first', 'saved'], ['second', 'saved']]);
    await chat.retrySaved(); // a retry while offline queues nothing new
    expect((await sync.outbox()).map((i) => i.clientId)).toEqual(['m1', 'm2']);

    state.online = true;
    state.pulls.push(
      empty({
        cursor: 4,
        changes: [
          { kind: 'chat_message', id: 's1', deleted: false, data: { id: 's1', role: 'user', text: 'first', clientId: 'm1', createdAt: '2026-01-05T09:00:00Z', replied: true } },
          { kind: 'chat_message', id: 's2', deleted: false, data: { id: 's2', role: 'user', text: 'second', clientId: 'm2', createdAt: '2026-01-05T09:00:01Z', replied: true } },
          { kind: 'chat_message', id: 's3', deleted: false, data: { id: 's3', role: 'coach', text: 'Hello', clientId: null, createdAt: '2026-01-05T09:00:02Z', replied: true } },
        ],
      }),
    );
    expect(await sync.sync()).toBe('synced');
    expect(state.sent).toEqual(['message:m1', 'message:m2']);

    await chat.load();
    expect(chat.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([['user', 'first', 'sent'], ['user', 'second', 'sent'], ['coach', 'Hello', 'sent']]);
    expect(chat.state.late).toBe(false);
    chat.dispose();
  });
});

describe('a message sent online', () => {
  const reply = (replied: boolean) =>
    empty({
      cursor: 2,
      changes: [
        { kind: 'chat_message' as const, id: 's1', deleted: false, data: { id: 's1', role: 'user', text: 'hi', clientId: 'm1', createdAt: '2026-01-05T09:00:00Z', replied } },
        ...(replied ? [{ kind: 'chat_message' as const, id: 's2', deleted: false, data: { id: 's2', role: 'coach', text: 'Hello', clientId: null, createdAt: '2026-01-05T09:00:02Z', replied: true } }] : []),
      ],
    });

  it('shows the Coach typing while it waits, then the reply, and never says the Coach is late', async () => {
    const { storage, sync, state } = phone();
    state.online = true;
    state.pulls.push(reply(true));
    const api = offlineCoachApi({ storage, sync, now: () => '2026-01-05T09:00:00Z' });
    const chat = createCoachChat(api, { newId: () => 'm1', now: () => '2026-01-05T09:00:00Z', retryDelaysMs: [1_000_000] });

    const sending = chat.send('hi');
    expect(chat.state.typing).toBe(true);
    expect(chat.state.late).toBe(false);
    await sending;
    await chat.load();

    expect(chat.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([['user', 'hi', 'sent'], ['coach', 'Hello', 'sent']]);
    expect(chat.state.typing).toBe(false);
    expect(chat.state.late).toBe(false);
    chat.dispose();
  });

  it('is Saved and the Coach late when the server kept it but had no reply', async () => {
    const { storage, sync, state } = phone();
    state.online = true;
    state.pulls.push(reply(false));
    const api = offlineCoachApi({ storage, sync, now: () => '2026-01-05T09:00:00Z' });
    const chat = createCoachChat(api, { newId: () => 'm1', now: () => '2026-01-05T09:00:00Z', retryDelaysMs: [1_000_000] });

    await chat.send('hi');

    expect(chat.state.messages.map((m) => [m.text, m.status])).toEqual([['hi', 'saved']]);
    expect(chat.state.late).toBe(true);
    chat.dispose();
  });
});

describe('Plan approvals', () => {
  const version = (id: string) => ({ id, status: 'proposed' }) as PlanVersionView;

  it('are queued when the server cannot be reached', async () => {
    const { sync } = phone();
    const api = offlinePlanApi({ sync, api: { get: async (id) => version(id) } });
    expect(await api.approve('v1')).toEqual({ kind: 'queued' });
    expect((await sync.outbox()).map((i) => i.kind)).toEqual(['approve_plan']);
  });

  it('a stale approval is dropped and the newer version is shown and cached', async () => {
    const { sync, storage, state } = phone({ send: () => ({ kind: 'rejected', status: 409, newer: { id: 'v2', status: 'proposed' } }) });
    state.online = true;
    // The server's pull from the start carries the newer version too.
    state.pulls.push(empty({ cursor: 3, changes: [{ kind: 'plan_version', id: 'v2', deleted: false, data: { id: 'v2', status: 'proposed' } }] }));
    const api = offlinePlanApi({ sync, api: { get: async (id) => version(id) } });
    expect(await api.approve('v1')).toEqual({ kind: 'stale', newer: { id: 'v2', status: 'proposed' }, reason: 'replaced' });
    expect(await sync.outbox()).toEqual([]);
    expect(await storage.list('plan_version')).toEqual([{ id: 'v2', status: 'proposed' }]);
  });

  it('an accepted approval returns the approved version', async () => {
    const { sync, state } = phone();
    state.online = true;
    const api = offlinePlanApi({ sync, api: { get: async (id) => ({ ...version(id), status: 'approved' }) } });
    expect(await api.approve('v1')).toEqual({ kind: 'approved', version: { id: 'v1', status: 'approved' } });
  });
});
