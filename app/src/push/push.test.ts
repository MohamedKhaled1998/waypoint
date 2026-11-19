import { REFRESH_LOOKS_GOOD, handleRefreshResponse, registerPushToken, routeForPush } from './push';

describe('where a push tap goes', () => {
  it('opens Plan review for "Plan ready", with the proposed version', () => {
    expect(routeForPush({ route: 'plan_ready', planVersionId: 'v1' })).toEqual({ pathname: '/plan-review', params: { id: 'v1' } });
  });

  it('opens Plan review on the first proposal for the weekly refresh prompt', () => {
    expect(routeForPush({ route: 'weekly_refresh', refreshId: 'r1', planVersionId: 'v7' })).toEqual({ pathname: '/plan-review', params: { id: 'v7' } });
    expect(routeForPush({ route: 'weekly_refresh', refreshId: 'r1' })).toBeNull();
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

describe('"Looks good" on the weekly refresh prompt', () => {
  const response = (actionIdentifier: string, data: unknown) => ({
    actionIdentifier,
    notification: { request: { identifier: 'n1', content: { dataString: data === null ? null : JSON.stringify(data) } } },
  });
  const deps = () => {
    const accepted: string[] = [];
    const dismissed: string[] = [];
    return {
      accepted,
      dismissed,
      deps: { accept: async (id: string) => void accepted.push(id), dismiss: async (id: string) => void dismissed.push(id) },
    };
  };

  it('accepts the refresh named in the push, from the shade, and clears the notification', async () => {
    const d = deps();
    expect(await handleRefreshResponse(response(REFRESH_LOOKS_GOOD, { route: 'weekly_refresh', refreshId: 'r1' }), d.deps)).toBe('accepted');
    expect(d.accepted).toEqual(['r1']);
    expect(d.dismissed).toEqual(['n1']);
  });

  it('leaves a body tap, another button and a push that is not a refresh alone', async () => {
    const d = deps();
    expect(await handleRefreshResponse(response('expo.modules.notifications.actions.DEFAULT', { route: 'weekly_refresh', refreshId: 'r1' }), d.deps)).toBe('ignored');
    expect(await handleRefreshResponse(response(REFRESH_LOOKS_GOOD, { route: 'coach' }), d.deps)).toBe('ignored');
    expect(await handleRefreshResponse(response(REFRESH_LOOKS_GOOD, null), d.deps)).toBe('ignored');
    expect(d.accepted).toEqual([]);
  });

  it('keeps the notification when the accept cannot be sent, so the tap is not lost', async () => {
    const dismissed: string[] = [];
    const out = await handleRefreshResponse(response(REFRESH_LOOKS_GOOD, { route: 'weekly_refresh', refreshId: 'r1' }), {
      accept: async () => {
        throw new Error('offline');
      },
      dismiss: async (id) => void dismissed.push(id),
    });
    expect(out).toBe('failed');
    expect(dismissed).toEqual([]);
  });
});
