/** What a push tap opens. The data comes from the server's push: a route name and row IDs, no free text. */
export type PushRoute = '/coach' | { pathname: '/plan-review'; params: { id: string } };

export function routeForPush(data: Record<string, unknown> | null | undefined): PushRoute | null {
  switch (data?.route) {
    case 'plan_ready':
      return typeof data.planVersionId === 'string' && data.planVersionId
        ? { pathname: '/plan-review', params: { id: data.planVersionId } }
        : null;
    case 'weekly_refresh':
      // The proposal to look at: the first one of the refresh.
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

/** The notification category and button the weekly refresh prompt carries. */
export const REFRESH_CATEGORY = 'weekly_refresh';
export const REFRESH_LOOKS_GOOD = 'looks_good';

export type RefreshResponse = {
  actionIdentifier: string;
  notification: { request: { identifier: string; content: { dataString?: string | null } } };
};

export type RefreshDeps = {
  /** Accepts the refresh's proposals on the server. Throws when it cannot be sent. */
  accept: (refreshId: string) => Promise<void>;
  dismiss: (notificationId: string) => Promise<void>;
};

/** "Looks good" in the shade, app killed or not: accepts the refresh named in the push. Any other tap is left to the app. */
export async function handleRefreshResponse(res: RefreshResponse, deps: RefreshDeps): Promise<'accepted' | 'ignored' | 'failed'> {
  if (res.actionIdentifier !== REFRESH_LOOKS_GOOD) return 'ignored';
  let refreshId: unknown;
  try {
    const data = JSON.parse(res.notification.request.content.dataString ?? 'null') as { route?: unknown; refreshId?: unknown } | null;
    refreshId = data?.route === 'weekly_refresh' ? data.refreshId : null;
  } catch {
    return 'ignored';
  }
  if (typeof refreshId !== 'string' || !refreshId) return 'ignored';
  try {
    await deps.accept(refreshId);
  } catch {
    // Still on the shade, and the proposal applies itself after a day anyway.
    return 'failed';
  }
  await deps.dismiss(res.notification.request.identifier);
  return 'accepted';
}
