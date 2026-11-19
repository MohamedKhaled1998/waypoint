import { fetch } from 'expo/fetch';
import type { Share } from './tracks';

const baseUrl = process.env.EXPO_PUBLIC_API_URL ?? '';

type Auth = { getAccessToken: () => string | null; renewAccess: () => Promise<boolean> };

export type SlotRow = {
  id: string;
  label: string;
  days: string[];
  start: string;
  end: string;
  hours: number;
  /** An edit waiting for the next Weekly refresh. */
  pending: { label: string; days: string[]; start: string; end: string } | null;
};
export type NewSlotRow = { id: string; label: string; days: string[]; start: string; end: string };

export type SettingsView = {
  timeBudgetHours: number | null;
  /** Asked for, waiting for the Coach's Re-plan to be approved. */
  requestedTimeBudgetHours: number | null;
  split: Share[];
  slots: SlotRow[];
  newSlots: NewSlotRow[];
  contactFrequency: 'Quiet' | 'Gentle' | 'Chatty' | null;
  quietStart: string;
  quietEnd: string;
  refreshDay: string;
  refreshTime: string;
  exactAlarmsAllowed: boolean | null;
};

export type SlotEditBody = { label?: string; days: string[]; start: string; end: string };

/** The Settings page's HTTP API. A 401 renews the access token once and retries. */
export function httpSettingsApi(auth: Auth) {
  async function call(path: string, method: string, body?: unknown) {
    const go = () =>
      fetch(`${baseUrl}${path}`, {
        method,
        body: body === undefined ? undefined : JSON.stringify(body),
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.getAccessToken() ?? ''}` },
      });
    let res = await go();
    if (res.status === 401 && (await auth.renewAccess())) res = await go();
    if (!res.ok) throw new Error(`Settings API ${res.status}`);
    return (await res.json()) as SettingsView;
  }
  return {
    get: () => call('/settings', 'GET'),
    setTimeBudget: (hours: number) => call('/settings/time-budget', 'PUT', { hours }),
    editSlot: (id: string, body: SlotEditBody) => call(`/settings/slots/${id}`, 'PUT', body),
    addSlot: (body: SlotEditBody) => call('/settings/slots', 'POST', body),
    setContactFrequency: (value: string) => call('/settings/contact-frequency', 'PUT', { value }),
    setQuietHours: (start: string, end: string) => call('/settings/quiet-hours', 'PUT', { start, end }),
    setWeeklyRefresh: (day: string, time: string) => call('/settings/weekly-refresh', 'PUT', { day, time }),
    reportExactAlarms: (allowed: boolean) => call('/settings/exact-alarms', 'PUT', { allowed }),
  };
}
export type SettingsApi = ReturnType<typeof httpSettingsApi>;
