import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { httpAuth } from './api';
import { clearGoogleAccount, getGoogleIdToken } from './google';
import { keystoreStorage } from './secureStorage';
import { createPhoneSync, phoneStorage, type PhoneSync } from '../sync/phone';
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
  /** The cache, outbox and pull; the same one for every screen. */
  phone: PhoneSync;
};

const SessionContext = createContext<Value | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const session = useMemo(() => createSession(keystoreStorage, httpAuth, () => phoneStorage.wipe()), []);
  const phone = useMemo(
    () => createPhoneSync({ getAccessToken: session.getAccessToken, renewAccess: () => session.restore().catch(() => false) }),
    [session],
  );
  const [status, setStatus] = useState<Status>('loading');

  useEffect(() => {
    session
      .restore()
      .then((ok) => setStatus(ok ? 'signedIn' : 'signedOut'))
      // The server is out of reach: a kept session starts from the cache, and the access token renews on the first call.
      .catch(async () => setStatus((await session.hasStoredSession().catch(() => false)) ? 'signedIn' : 'signedOut'));
  }, [session]);

  const value = useMemo<Value>(
    () => ({
      status,
      getAccessToken: session.getAccessToken,
      renewAccess: () => session.restore().catch(() => false),
      phone,
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
    [session, phone, status],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): Value {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession must be used inside SessionProvider');
  return value;
}
