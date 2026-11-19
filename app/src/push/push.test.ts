import { registerPushToken, routeForPush } from './push';

describe('where a push tap goes', () => {
  it('opens Plan review for "Plan ready", with the proposed version', () => {
    expect(routeForPush({ route: 'plan_ready', planVersionId: 'v1' })).toEqual({ pathname: '/plan-review', params: { id: 'v1' } });
  });

  it('opens the Coach chat for a Coach message', () => {
    expect(routeForPush({ route: 'coach' })).toBe('/coach');
  });

  it('ignores anything else, including a reminder', () => {
    expect(routeForPush({ actionId: 'a1' })).toBeNull();
    expect(routeForPush(null)).toBeNull();
    expect(routeForPush({ route: 'nowhere' })).toBeNull();
  });
});

describe('registering the FCM token', () => {
  function setup(first: string | null) {
    const sent: string[] = [];
    let refresh: ((t: string) => void) | undefined;
    const stop = jest.fn();
    const done = registerPushToken({
      getToken: async () => first,
      onRefresh: (cb) => {
        refresh = cb;
        return stop;
      },
      send: async (t) => void sent.push(t),
    });
    return { sent, refresh: (t: string) => refresh!(t), done, stop };
  }

  it('sends the token after sign-in and again on refresh', async () => {
    const s = setup('tok-1');
    await s.done.ready;
    expect(s.sent).toEqual(['tok-1']);
    s.refresh('tok-2');
    await Promise.resolve();
    expect(s.sent).toEqual(['tok-1', 'tok-2']);
    s.done.stop();
    expect(s.stop).toHaveBeenCalled();
  });

  it('sends nothing when the phone has no token yet', async () => {
    const s = setup(null);
    await s.done.ready;
    expect(s.sent).toEqual([]);
  });

  it('survives a failed send, so the next refresh still goes out', async () => {
    const sent: string[] = [];
    let n = 0;
    const r = registerPushToken({
      getToken: async () => 'a',
      onRefresh: () => () => {},
      send: async (t) => {
        if (n++ === 0) throw new Error('offline');
        sent.push(t);
      },
    });
    await expect(r.ready).resolves.toBeUndefined();
    expect(sent).toEqual([]);
  });
});
