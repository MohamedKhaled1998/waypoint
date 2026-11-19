export type TokenPair = { accessToken: string; refreshToken: string };

/** Keystore-backed storage on the phone (expo-secure-store in the app). */
export interface SecureStorage {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  remove(key: string): Promise<void>;
}

/** The sign-in half of the API. `refresh` resolves null when the server rejects the token. */
export interface ApiAuth {
  signInWithGoogle(idToken: string): Promise<TokenPair>;
  refresh(refreshToken: string): Promise<TokenPair | null>;
  signOut(refreshToken: string): Promise<void>;
}

const REFRESH_KEY = 'refreshToken';

/** `wipe` clears what the phone keeps for the user (cache and outbox); it runs on every sign-out, online or not. */
export function createSession(storage: SecureStorage, api: ApiAuth, wipe: () => Promise<void> = async () => {}) {
  let accessToken: string | null = null;
  let renewing: Promise<boolean> | null = null;

  async function adopt(tokens: TokenPair) {
    accessToken = tokens.accessToken;
    await storage.set(REFRESH_KEY, tokens.refreshToken);
  }

  return {
    getAccessToken: () => accessToken,

    async signIn(googleIdToken: string) {
      await adopt(await api.signInWithGoogle(googleIdToken));
    },

    /** True when a refresh token is kept: the phone can start from its cache while the server is out of reach. */
    async hasStoredSession(): Promise<boolean> {
      return (await storage.get(REFRESH_KEY)) !== null;
    },

    /**
     * Trades the stored refresh token for new tokens: on launch, and after a 401. True when the
     * phone is signed in afterwards; false (and wiped) when the server rejects the token; throws
     * when the server can't be reached. Concurrent calls share one refresh, because the server
     * rotates the token and treats a second use of the old one as theft.
     */
    renew(): Promise<boolean> {
      renewing ??= (async () => {
        const stored = await storage.get(REFRESH_KEY);
        if (!stored) return false;
        const tokens = await api.refresh(stored);
        if (!tokens) {
          accessToken = null;
          await storage.remove(REFRESH_KEY);
          return false;
        }
        await adopt(tokens);
        return true;
      })().finally(() => {
        renewing = null;
      });
      return renewing;
    },

    async signOut() {
      const stored = await storage.get(REFRESH_KEY);
      accessToken = null;
      await storage.remove(REFRESH_KEY);
      await wipe();
      if (stored) {
        try {
          await api.signOut(stored);
        } catch {
          // Offline: the phone is already wiped; the server token expires on its own.
        }
      }
    },
  };
}

export type Session = ReturnType<typeof createSession>;
