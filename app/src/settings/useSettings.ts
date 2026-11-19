import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { phonePermissions } from '../reminders/native';
import { httpSettingsApi, type SettingsView } from './api';

/** The Settings page's data: loaded once, and every change answers with the page as the server now has it. */
export function useSettings() {
  const session = useSession();
  const api = useMemo(() => httpSettingsApi(session), [session]);
  const [settings, setSettings] = useState<SettingsView | null>(null);
  const [failed, setFailed] = useState(false);
  const [exactAlarms, setExactAlarms] = useState<boolean | null>(null);

  const run = useCallback(async (change: Promise<SettingsView>) => {
    try {
      setSettings(await change);
      setFailed(false);
    } catch {
      setFailed(true);
    }
  }, []);

  const checkAlarms = useCallback(async () => {
    const allowed = await phonePermissions.granted('exactAlarms').catch(() => null);
    setExactAlarms(allowed);
    if (allowed !== null) api.reportExactAlarms(allowed).then(setSettings).catch(() => {});
  }, [api]);

  useEffect(() => {
    void run(api.get());
    void checkAlarms();
  }, [api, run, checkAlarms]);

  return {
    settings, failed, exactAlarms, api, run,
    async allowExactAlarms() {
      await phonePermissions.request('exactAlarms');
      await checkAlarms();
    },
  };
}
