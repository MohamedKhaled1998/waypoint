import { httpRemote } from './remote';
import type { OutboxItem } from './storage';

const mockFetch = jest.fn();
jest.mock('expo/fetch', () => ({ fetch: (...args: unknown[]) => mockFetch(...args) }));

const res = (status: number, body: unknown = null) => ({
  status,
  ok: status >= 200 && status < 300,
  json: async () => body,
  text: async () => '',
});
const item = (kind: OutboxItem['kind'], payload: OutboxItem['payload'] = {}): OutboxItem => ({ clientId: 'c1', kind, payload, queuedAt: '' });
const remote = () => httpRemote({ getAccessToken: () => 't', renewAccess: async () => false });

describe('the outbox transport', () => {
  beforeEach(() => mockFetch.mockReset());

  it.each([401, 408, 429, 500, 502, 503])('%i is a retry: the item is not dropped', async (status) => {
    mockFetch.mockResolvedValue(res(status));
    await expect(remote().send(item('check_in'))).rejects.toThrow();
  });

  it('a network failure is a retry', async () => {
    mockFetch.mockRejectedValue(new Error('offline'));
    await expect(remote().send(item('check_in'))).rejects.toThrow();
  });

  it('2xx is sent', async () => {
    mockFetch.mockResolvedValue(res(200));
    expect(await remote().send(item('check_in'))).toEqual({ kind: 'sent' });
  });

  it('409 is a rejection that carries the newer version', async () => {
    mockFetch.mockResolvedValue(res(409, { newer: { id: 'v2' }, reason: 'replaced' }));
    expect(await remote().send(item('approve_plan', { versionId: 'v1' }))).toEqual({ kind: 'rejected', status: 409, newer: { id: 'v2' } });
  });

  it('another 4xx is a rejection for good', async () => {
    mockFetch.mockResolvedValue(res(400));
    expect(await remote().send(item('move_action', { scheduledActionId: 'a', date: 'd', slotId: 's' }))).toEqual({ kind: 'rejected', status: 400, newer: undefined });
  });
});
