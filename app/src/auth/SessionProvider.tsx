import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { httpAuth } from './api';
import { clearGoogleAccount, getGoogleIdToken } from './google';
import { keystoreStorage } from './secureStorage';
import { createPhoneSync, phoneStorage, type PhoneSync } from '../sync/phone';
import { forgetPushToken } from '../push/native';
import { firstRun } from '../firstrun/native';
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
    () => createPhoneSync({ getAccessToken: session.getAccessToken, renewAccess: () => session.renew().catch(() => false) }),
    [session],
  );
  const [status, setStatus] = useState<Status>('loading');

  useEffect(() => {
    session
      .renew()
      .then((ok) => setStatus(ok ? 'signedIn' : 'signedOut'))
      // The server is out of reach: a kept session starts from the cache, and the access token renews on the first call.
      .catch(async () => setStatus((await session.hasStoredSession().catch(() => false)) ? 'signedIn' : 'signedOut'));
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
        await forgetPushToken({ getAccessToken: session.getAccessToken, renewAccess: () => session.renew().catch(() => false) });
        await session.signOut();
        await firstRun.reset();
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
