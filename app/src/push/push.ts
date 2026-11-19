/** What a push tap opens. The data comes from the server's push: a route name and row IDs, no free text. */
export type PushRoute = '/coach' | { pathname: '/plan-review'; params: { id: string } };

export function routeForPush(data: Record<string, unknown> | null | undefined): PushRoute | null {
  switch (data?.route) {
    case 'plan_ready':
      return typeof data.planVersionId === 'string' && data.planVersionId
        ? { pathname: '/plan-review', params: { id: data.planVersionId } }
        : null;
    case 'coach':
      return '/coach';
    default:
      return null;
  }
}

export type PushRegistration = {
  getToken: () => Promise<string | null>;
  onRefresh: (cb: (token: string) => void) => () => void;
  send: (token: string) => Promise<void>;
};

/** Sends the FCM token to the API now and whenever the phone gets a new one. A failed send is tried again at the next start or refresh. */
export function registerPushToken(reg: PushRegistration): { ready: Promise<void>; stop: () => void } {
  const trySend = async (token: string | null) => {
    if (!token) return;
    try {
      await reg.send(token);
    } catch {
      console.warn('push: registering the token failed');
    }
  };
  const stopRefresh = reg.onRefresh((t) => void trySend(t));
  const ready = reg.getToken().then(trySend, () => console.warn('push: no token'));
  return { ready, stop: stopRefresh };
}
