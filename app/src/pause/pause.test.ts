import { toArm } from '../reminders/arming';
import { createSync, type Pull, type Remote } from '../sync/engine';
import { createMemoryStorage } from '../sync/storage';
import type { ScheduledActionView } from '../today/today';
import { batteryHintFor, remindersLookDead } from './battery';
import { createOfflinePause, pausedState, progressRows, todayPausedLine, type PauseView } from './pause';

const action = (over: Partial<ScheduledActionView> = {}): ScheduledActionView => ({
  id: 'a1', aspirationId: 'asp1', date: '2026-01-07', slotId: 's1', slot: 'mornings', slotStart: '07:00', slotEnd: '07:45',
  title: 'Easy run', minutes: 30, checkIn: null, reminderKind: 'now', reminderLeadMinutes: null, reminderText: null, reminderOn: true,
  ...over,
});
const NOW = new Date(2026, 0, 6, 20, 0); // the night before

function phone(actions: ScheduledActionView[]) {
  const storage = createMemoryStorage();
  const sent: string[] = [];
  const remote: Remote = {
    async send(item) {
      sent.push(`${item.kind}:${JSON.stringify(item.payload)}`);
      throw new Error('offline');
    },
    async pull(cursor): Promise<Pull> {
      return { cursor, more: false, changes: [], slots: [] };
    },
  };
  let n = 0;
  const sync = createSync({ storage, remote, now: () => NOW, newId: () => `c-${++n}` });
  return { storage, sync, sent, ready: storage.apply({ upserts: actions.map((a) => ({ kind: 'scheduled_action' as const, id: a.id, data: a })), removes: [] }) };
}

const armed = async (storage: ReturnType<typeof createMemoryStorage>) =>
  toArm(await storage.list<ScheduledActionView>('scheduled_action'), NOW).flatMap((g) => g.reminders.map((r) => r.actionId));

describe('Pause on the phone', () => {
  it('cancels the reminders of the Actions it sets aside, and Resume arms them again', async () => {
    const { storage, sync, ready } = phone([
      action(),
      action({ id: 'a2', aspirationId: 'asp2', title: 'Spanish' }),
      action({ id: 'a3', date: '2026-01-12' }),
    ]);
    await ready;
    const api = createOfflinePause({ storage, sync, now: () => NOW, newId: () => 'pc-1' });
    expect(await armed(storage)).toEqual(['a1', 'a2', 'a3']);

    // One Aspiration, until the end of Friday the 9th: only its Actions inside the Pause are set aside.
    await api.start({ aspirationId: 'asp1' }, { days: 4 });
    expect(await armed(storage)).toEqual(['a2', 'a3']);

    await api.resume('pc-1');
    expect(await armed(storage)).toEqual(['a1', 'a2', 'a3']);
  });

  it('"Just this Action" and "rest of today" pause only what they name', async () => {
    const { storage, sync, ready } = phone([action(), action({ id: 'a2', title: 'Stretch' }), action({ id: 'a3', date: '2026-01-08' })]);
    await ready;
    const api = createOfflinePause({ storage, sync, now: () => new Date(2026, 0, 7, 6, 0), newId: () => 'pc-2' });
    await api.start({ actionId: 'a1' }, 'today');
    expect(await armed(storage)).toEqual(['a2', 'a3']);
    await api.start({}, 'today');
    expect(await armed(storage)).toEqual(['a3']); // tomorrow is not inside "not today"
  });

  it('queues the Pause and the Resume in order for the server, by client ID', async () => {
    const { storage, sync, ready } = phone([action()]);
    await ready;
    const api = createOfflinePause({ storage, sync, now: () => NOW, newId: () => 'pc-3' });
    await api.start({}, 'open');
    await api.resume('pc-3');
    const items = await sync.outbox();
    expect(items.map((i) => i.kind)).toEqual(['pause', 'resume']);
    expect(items[0]!.clientId).toBe('pc-3');
    expect(items[0]!.payload).toEqual({ open: true });
    expect(items[1]!.payload).toEqual({ pauseClientId: 'pc-3' });
  });

  it('a Check-in is never set aside', async () => {
    const checkIn = { state: 'done' as const, minutes: null, reasons: [], note: null, tappedAt: '2026-01-06T10:00:00Z' };
    const { storage, sync, ready } = phone([action({ checkIn })]);
    await ready;
    await createOfflinePause({ storage, sync, now: () => NOW, newId: () => 'pc-4' }).start({}, { days: 3 });
    expect((await storage.list<ScheduledActionView>('scheduled_action'))[0]!.setAside).toBeFalsy();
  });
});

describe('the paused states', () => {
  const all: PauseView = { id: 'p1', clientId: 'c1', aspirationId: null, actionId: null, startsAt: '2026-01-06T09:00:00Z', endsAt: '2026-01-09T00:00:00Z', startedBy: 'user' };
  const one: PauseView = { id: 'p2', clientId: 'c2', aspirationId: 'asp1', actionId: null, startsAt: '2026-01-06T09:00:00Z', endsAt: null, startedBy: 'user' };
  const act: PauseView = { id: 'p3', clientId: 'c3', aspirationId: null, actionId: 'a1', startsAt: '2026-01-06T09:00:00Z', endsAt: '2026-01-07T00:00:00Z', startedBy: 'user' };
  const aspirations = [
    { aspirationId: 'asp1', shortName: 'Half marathon', hue: 'coral' },
    { aspirationId: 'asp2', shortName: 'Spanish', hue: 'teal' },
  ];

  it('Today: everything paused says until when, with Resume now; one paused Aspiration is one quiet line', () => {
    expect(pausedState([all], aspirations)).toEqual({ everything: { pauseId: 'c1', until: '2026-01-08', byCoach: false }, aspirations: [] });
    expect(todayPausedLine(pausedState([one, act], aspirations), aspirations)).toEqual({ kind: 'aspirations', names: ['Half marathon'] });
    expect(todayPausedLine(pausedState([all], aspirations), aspirations)).toEqual({ kind: 'everything', until: '2026-01-08', byCoach: false, pauseId: 'c1' });
    expect(todayPausedLine(pausedState([], aspirations), aspirations)).toBeNull();
  });

  it('Progress: paused Aspirations are dashed rows with Resume, an open Pause has no date', () => {
    expect(progressRows(aspirations, pausedState([one], aspirations))).toEqual([
      { aspirationId: 'asp1', shortName: 'Half marathon', hue: 'coral', paused: { pauseId: 'c2', until: null } },
      { aspirationId: 'asp2', shortName: 'Spanish', hue: 'teal', paused: null },
    ]);
  });

  it('when everything is paused, every Aspiration shows as paused', () => {
    expect(progressRows(aspirations, pausedState([all], aspirations)).map((r) => r.paused?.until)).toEqual(['2026-01-08', '2026-01-08']);
  });
});

describe('reminders that do not land', () => {
  it('the server\'s second silence nudge carries the battery hint', () => {
    expect(batteryHintFor({ route: 'coach', hint: 'battery_settings' })).toBe(true);
    expect(batteryHintFor({ route: 'coach' })).toBe(false);
  });

  it('Check-in taps long after their reminder suggest the phone is killing Waypoint', () => {
    const late = (i: number) =>
      action({ id: `l${i}`, date: '2026-01-07', slotStart: '07:00', checkIn: { state: 'done', minutes: null, reasons: [], note: null, tappedAt: new Date(2026, 0, 7, 12, 0).toISOString() } });
    const prompt = (i: number) =>
      action({ id: `p${i}`, date: '2026-01-07', slotStart: '07:00', checkIn: { state: 'done', minutes: null, reasons: [], note: null, tappedAt: new Date(2026, 0, 7, 7, 3).toISOString() } });
    expect(remindersLookDead([late(1), late(2), late(3), prompt(4)])).toBe(true);
    expect(remindersLookDead([prompt(1), prompt(2), prompt(3), late(4)])).toBe(false);
    expect(remindersLookDead([late(1), late(2)])).toBe(false); // too little to go on
  });
});
