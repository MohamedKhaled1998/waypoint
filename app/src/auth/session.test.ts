import { createSession, type SecureStorage, type ApiAuth } from './session';

class MemoryStorage implements SecureStorage {
  items = new Map<string, string>();
  async get(key: string) {
    return this.items.get(key) ?? null;
  }
  async set(key: string, value: string) {
    this.items.set(key, value);
  }
  async remove(key: string) {
    this.items.delete(key);
  }
}

function fakeApi(): ApiAuth & { issued: number; revoked: string[] } {
  const api = {
    issued: 0,
    revoked: [] as string[],
    async signInWithGoogle(idToken: string) {
      if (idToken !== 'good') throw new Error('rejected');
      api.issued += 1;
      return { accessToken: `access-${api.issued}`, refreshToken: `refresh-${api.issued}` };
    },
    async refresh(refreshToken: string) {
      if (refreshToken !== `refresh-${api.issued}`) return null;
      api.issued += 1;
      return { accessToken: `access-${api.issued}`, refreshToken: `refresh-${api.issued}` };
    },
    async signOut(refreshToken: string) {
      api.revoked.push(refreshToken);
    },
  };
  return api;
}

describe('session', () => {
  it('signing in keeps the refresh token in secure storage and the access token in memory', async () => {
    const storage = new MemoryStorage();
    const session = createSession(storage, fakeApi());
    await session.signIn('good');
    expect(session.getAccessToken()).toBe('access-1');
    expect(await storage.get('refreshToken')).toBe('refresh-1');
  });

  it('a rejected Google token leaves the phone signed out', async () => {
    const storage = new MemoryStorage();
    const session = createSession(storage, fakeApi());
    await expect(session.signIn('bad')).rejects.toThrow();
    expect(session.getAccessToken()).toBeNull();
    expect(await storage.get('refreshToken')).toBeNull();
  });

  it('renewing after a restart rotates the stored refresh token', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    await createSession(storage, api).signIn('good');

    const restarted = createSession(storage, api);
    expect(await restarted.renew()).toBe(true);
    expect(restarted.getAccessToken()).toBe('access-2');
    expect(await storage.get('refreshToken')).toBe('refresh-2');
  });

  it('renewing with a refresh token the server no longer accepts signs out and wipes it', async () => {
    const storage = new MemoryStorage();
    await storage.set('refreshToken', 'stale');
    const session = createSession(storage, fakeApi());
    expect(await session.renew()).toBe(false);
    expect(await storage.get('refreshToken')).toBeNull();
  });

  it('renewing with nothing stored is signed out', async () => {
    const session = createSession(new MemoryStorage(), fakeApi());
    expect(await session.renew()).toBe(false);
  });

  it('signing out revokes on the server and wipes the refresh token', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    const session = createSession(storage, api);
    await session.signIn('good');
    await session.signOut();
    expect(api.revoked).toEqual(['refresh-1']);
    expect(await storage.get('refreshToken')).toBeNull();
    expect(session.getAccessToken()).toBeNull();
  });

  it('signing out still wipes the phone when the server is unreachable', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    api.signOut = async () => {
      throw new Error('offline');
    };
    const session = createSession(storage, api);
    await session.signIn('good');
    await session.signOut();
    expect(await storage.get('refreshToken')).toBeNull();
  });

  it('parallel renewals after a 401 share one refresh, so the rotated token is never reused', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    const session = createSession(storage, api);
    await session.signIn('good');

    const results = await Promise.all([session.renew(), session.renew(), session.renew()]);
    expect(results).toEqual([true, true, true]);
    expect(api.issued).toBe(2);
    expect(session.getAccessToken()).toBe('access-2');
    expect(await storage.get('refreshToken')).toBe('refresh-2');

    expect(await session.renew()).toBe(true);
    expect(session.getAccessToken()).toBe('access-3');
  });

  it('a renewal the server rejects drops the access token and wipes the refresh token', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    const session = createSession(storage, api);
    await session.signIn('good');
    api.issued += 1; // the server has moved on: the stored token is no longer current

    expect(await session.renew()).toBe(false);
    expect(session.getAccessToken()).toBeNull();
    expect(await storage.get('refreshToken')).toBeNull();
  });

  it('a renewal that cannot reach the server keeps the session and lets the next one retry', async () => {
    const storage = new MemoryStorage();
    const api = fakeApi();
    const session = createSession(storage, api);
    await session.signIn('good');
    const refresh = api.refresh;
    api.refresh = async () => {
      throw new Error('offline');
    };

    await expect(session.renew()).rejects.toThrow('offline');
    expect(await storage.get('refreshToken')).toBe('refresh-1');

    api.refresh = refresh;
    expect(await session.renew()).toBe(true);
  });

  it('signing out wipes the cache and outbox, even when the server cannot be reached', async () => {
    const api = fakeApi();
    let wiped = 0;
    const session = createSession(new MemoryStorage(), api, async () => void wiped++);
    await session.signIn('good');
    api.signOut = async () => {
      throw new Error('offline');
    };
    await session.signOut();
    expect(wiped).toBe(1);
  });

  it('knows a session is kept even when it cannot be resumed yet', async () => {
    const storage = new MemoryStorage();
    const session = createSession(storage, fakeApi());
    expect(await session.hasStoredSession()).toBe(false);
    await session.signIn('good');
    expect(await session.hasStoredSession()).toBe(true);
    await session.signOut();
    expect(await session.hasStoredSession()).toBe(false);
  });
});
