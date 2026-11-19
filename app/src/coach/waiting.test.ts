import { createSync, type Pull, type Remote } from '../sync/engine';
import { createMemoryStorage } from '../sync/storage';
import { createOfflinePause, type PauseView } from '../pause/pause';
import type { PlanSummary } from '../plan/review';
import type { ChatMessage } from './chat';
import { pauseFromCard, waitingItems, type PauseCardData } from './waiting';

const NOW = new Date(2026, 0, 6, 20, 0);
const card = (over: Partial<PauseCardData> = {}): PauseCardData => ({
  clientId: 'chat-1', aspirationId: null, span: 'until', endsOn: '2026-01-09', shortName: null, ...over,
});
const message = (over: Partial<ChatMessage>): ChatMessage => ({
  id: 'm1', role: 'coach', text: '', createdAt: NOW.toISOString(), status: 'sent', ...over,
});
const plan = (pendingVersionId: string | null): PlanSummary => ({
  aspirationId: 'a1', shortName: 'Half', hue: 'coral', drafting: false, draftLate: false, pendingVersionId, currentVersionId: 'v0',
});

describe('a Pause the Coach suggests', () => {
  it('starts with one tap from the card: the scope and end it read, under the card client ID', async () => {
    const storage = createMemoryStorage();
    const sent: string[] = [];
    const remote: Remote = {
      async send(item) {
        sent.push(`${item.kind}:${item.clientId}:${JSON.stringify(item.payload)}`);
        throw new Error('offline');
      },
      async pull(cursor): Promise<Pull> {
        return { cursor, more: false, changes: [], slots: [] };
      },
    };
    let n = 0;
    const sync = createSync({ storage, remote, now: () => NOW, newId: () => `c-${++n}` });
    const pause = createOfflinePause({ storage, sync, now: () => NOW, newId: () => 'phone-id' });

    const { scope, until, clientId } = pauseFromCard(card({ aspirationId: 'asp1' }));
    const started = await pause.start(scope, until, clientId);
    await pause.start(scope, until, clientId); // a second tap starts nothing new

    expect(started.clientId).toBe('chat-1');
    expect(started.aspirationId).toBe('asp1');
    expect(started.endsAt).toBe('2026-01-10T00:00:00.000Z');
    expect(await pause.pauses()).toHaveLength(1);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toContain('"endsOn":"2026-01-09"');
  });

  it('maps "open" and "rest of today"', () => {
    expect(pauseFromCard(card({ span: 'open', endsOn: null })).until).toBe('open');
    expect(pauseFromCard(card({ span: 'today', endsOn: null })).until).toBe('today');
  });
});

describe('the pinned bar', () => {
  it('holds proposals still pending and Pause suggestions not started', () => {
    const messages = [
      message({ id: 'p1', card: 'proposal', cardData: { planVersionId: 'v1', shortName: 'Half', hue: 'coral', reason: 'time_budget_cut' } }),
      message({ id: 'p0', card: 'proposal', cardData: { planVersionId: 'v0', shortName: 'Half', hue: 'coral' } }),
      message({ id: 's1', card: 'pause_suggestion', cardData: card() }),
    ];
    const items = waitingItems(messages, [plan('v1')], [], NOW);
    expect(items.map((i) => i.key)).toEqual(['p1', 's1']);
    expect(items[0]).toMatchObject({ kind: 'proposal', versionId: 'v1', reason: 'time_budget_cut' });
  });

  it('drops a Pause suggestion once it is started, past its last day or a day old', () => {
    const suggestion = message({ id: 's1', card: 'pause_suggestion', cardData: card() });
    const started: PauseView[] = [{ id: 'x', clientId: 'chat-1', aspirationId: null, actionId: null, startsAt: NOW.toISOString(), endsAt: null, startedBy: 'user' }];
    expect(waitingItems([suggestion], [], started, NOW)).toEqual([]);
    expect(waitingItems([suggestion], [], [], new Date(2026, 0, 10, 8, 0))).toEqual([]);
    expect(waitingItems([message({ ...suggestion, createdAt: new Date(2026, 0, 4).toISOString() })], [], [], NOW)).toEqual([]);
  });
});
