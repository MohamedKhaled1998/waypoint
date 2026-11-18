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

export function createSession(storage: SecureStorage, api: ApiAuth) {
  let accessToken: string | null = null;

  async function adopt(tokens: TokenPair) {
    accessToken = tokens.accessToken;
    await storage.set(REFRESH_KEY, tokens.refreshToken);
  }

  return {
    getAccessToken: () => accessToken,

    async signIn(googleIdToken: string) {
      await adopt(await api.signInWithGoogle(googleIdToken));
    },

    /** Resumes a previous sign-in; true when the phone is signed in afterwards. */
    async restore(): Promise<boolean> {
      const stored = await storage.get(REFRESH_KEY);
      if (!stored) return false;
      const tokens = await api.refresh(stored);
      if (!tokens) {
        await storage.remove(REFRESH_KEY);
        return false;
      }
      await adopt(tokens);
      return true;
    },

    async signOut() {
      const stored = await storage.get(REFRESH_KEY);
      accessToken = null;
      await storage.remove(REFRESH_KEY);
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
