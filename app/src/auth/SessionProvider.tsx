import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { httpAuth } from './api';
import { clearGoogleAccount, getGoogleIdToken } from './google';
import { keystoreStorage } from './secureStorage';
import { createSession } from './session';

type Status = 'loading' | 'signedOut' | 'signedIn';
type SignInResult = 'ok' | 'cancelled' | 'failed';

type Value = { status: Status; signIn: () => Promise<SignInResult>; signOut: () => Promise<void> };

const SessionContext = createContext<Value | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const session = useMemo(() => createSession(keystoreStorage, httpAuth), []);
  const [status, setStatus] = useState<Status>('loading');

  useEffect(() => {
    session
      .restore()
      .then((ok) => setStatus(ok ? 'signedIn' : 'signedOut'))
      // Offline on start: keep the stored token and show sign-in; the next launch retries.
      .catch(() => setStatus('signedOut'));
  }, [session]);

  const value = useMemo<Value>(
    () => ({
      status,
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
