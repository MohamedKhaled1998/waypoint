import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { httpAuth } from './api';
import { clearGoogleAccount, getGoogleIdToken } from './google';
import { keystoreStorage } from './secureStorage';
import { createSession } from './session';

type Status = 'loading' | 'signedOut' | 'signedIn';
type SignInResult = 'ok' | 'cancelled' | 'failed';

type Value = {
  status: Status;
  /** The current access token (null when signed out) and a way to renew it after a 401. */
  getAccessToken: () => string | null;
  renewAccess: () => Promise<boolean>;
  signIn: () => Promise<SignInResult>;
  signOut: () => Promise<void>;
};

const SessionContext = createContext<Value | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const session = useMemo(() => createSession(keystoreStorage, httpAuth), []);
  const [status, setStatus] = useState<Status>('loading');

  useEffect(() => {
    session
      .renew()
      .then((ok) => setStatus(ok ? 'signedIn' : 'signedOut'))
      // Offline on start: keep the stored token and show sign-in; the next launch retries.
      .catch(() => setStatus('signedOut'));
  }, [session]);

  const value = useMemo<Value>(
    () => ({
      status,
      getAccessToken: session.getAccessToken,
      async renewAccess() {
        try {
          const ok = await session.renew();
          // The server rejected the refresh token: the phone is wiped, so show sign-in.
          if (!ok) setStatus('signedOut');
          return ok;
        } catch {
          // Offline: keep the session and let the call fail; the next 401 tries again.
          return false;
        }
      },
      async signIn() {
        try {
          const idToken = await getGoogleIdToken();
          if (!idToken) return 'cancelled';
          await session.signIn(idToken);
          setStatus('signedIn');
          return 'ok';
        } catch {
          return 'failed';
        }
      },
      async signOut() {
        await session.signOut();
        await clearGoogleAccount();
        setStatus('signedOut');
      },
    }),
    [session, status],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): Value {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession must be used inside SessionProvider');
  return value;
}
