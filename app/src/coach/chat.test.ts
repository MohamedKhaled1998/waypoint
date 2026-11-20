import { createCoachChat, groupByDay, type CoachApi, type StoredMessage } from './chat';
import type { SseEvent } from './sse';

type Script = (onEvent: (e: SseEvent) => void) => Promise<void>;

function fakeApi() {
  const api = {
    server: [] as StoredMessage[],
    sent: [] as string[],
    contexts: [] as unknown[],
    script: (async () => {}) as Script,
    async history() {
      return api.server;
    },
    async send(_text: string, clientId: string, onEvent: (e: SseEvent) => void, aspirationId?: string, intent?: string) {
      api.sent.push(clientId);
      api.contexts.push({ aspirationId, intent });
      await api.script(onEvent);
    },
  };
  return api satisfies CoachApi & Record<string, unknown>;
}

let n = 0;
const chat = (api: CoachApi) => createCoachChat(api, { newId: () => `c${++n}`, now: () => '2026-01-05T09:00:00Z' });

describe('Coach chat', () => {
  it('sends the Aspiration the message is about, so the filter keeps it', async () => {
    const api = fakeApi();
    const seen: (string | undefined)[] = [];
    api.send = async (_t: string, clientId: string, _e: (e: SseEvent) => void, aspirationId?: string) => {
      api.sent.push(clientId);
      seen.push(aspirationId);
    };
    const c = chat(api);

    await c.send('About running', 'asp-1');

    expect(seen).toEqual(['asp-1']);
    expect(c.state.messages[0]?.aspirationId).toBe('asp-1');
  });

  it('sends the Aspiration and the intent with the message, and again when it is resent', async () => {
    const api = fakeApi();
    let fail = true;
    api.script = async (emit) => {
      if (fail) throw new Error('offline');
      emit({ name: 'done', data: { messageId: 'm1' } });
    };
    const c = chat(api);
    await c.send('I would like to change the dates.', 'a1', 'change_dates');
    fail = false;
    await c.retrySaved();
    expect(api.contexts).toEqual([
      { aspirationId: 'a1', intent: 'change_dates' },
      { aspirationId: 'a1', intent: 'change_dates' },
    ]);
  });

  it('shows typing, then streams the reply into one Coach message', async () => {
    const api = fakeApi();
    const seen: { typing: boolean; last: string | undefined }[] = [];
    api.script = async (emit) => {
      seen.push({ typing: c.state.typing, last: c.state.messages.at(-1)?.text });
      emit({ name: 'delta', data: { text: 'Hel' } });
      seen.push({ typing: c.state.typing, last: c.state.messages.at(-1)?.text });
      emit({ name: 'delta', data: { text: 'lo' } });
      emit({ name: 'done', data: { messageId: 'm1' } });
    };
    const c = chat(api);

    await c.send('Hi');

    expect(seen).toEqual([
      { typing: true, last: 'Hi' },
      { typing: false, last: 'Hel' },
    ]);
    expect(c.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([
      ['user', 'Hi', 'sent'],
      ['coach', 'Hello', 'sent'],
    ]);
    expect(c.state.late).toBe(false);
  });

  it('keeps the message as Saved and says the Coach is late when the server cannot be reached', async () => {
    const api = fakeApi();
    api.script = async () => {
      throw new TypeError('Network request failed');
    };
    const c = chat(api);

    await c.send('Are you there?');

    expect(c.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([['user', 'Are you there?', 'saved']]);
    expect(c.state.late).toBe(true);
    expect(c.state.typing).toBe(false);
  });

  it('treats an error event the same way, and fakes no Coach line', async () => {
    const api = fakeApi();
    api.script = async (emit) => emit({ name: 'error', data: { type: 'coach_unavailable' } });
    const c = chat(api);
    await c.send('Hi');
    expect(c.state.messages.every((m) => m.role === 'user')).toBe(true);
    expect(c.state.messages[0]?.status).toBe('saved');
    expect(c.state.late).toBe(true);
  });

  it('resends saved messages with the same client ID once the call succeeds', async () => {
    const api = fakeApi();
    api.script = async () => {
      throw new TypeError('offline');
    };
    const c = chat(api);
    await c.send('Hi');

    api.script = async (emit) => {
      emit({ name: 'delta', data: { text: 'Welcome back' } });
      emit({ name: 'done', data: { messageId: 'm1' } });
    };
    await c.retrySaved();

    expect(api.sent).toEqual([api.sent[0], api.sent[0]]);
    expect(c.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([
      ['user', 'Hi', 'sent'],
      ['coach', 'Welcome back', 'sent'],
    ]);
    expect(c.state.late).toBe(false);
  });

  it('loads the stored thread and keeps unsent local messages after it', async () => {
    const api = fakeApi();
    api.server = [
      { id: 'a', role: 'user', text: 'Earlier', clientId: 'old', createdAt: '2026-01-04T09:00:00Z', replied: true },
      { id: 'b', role: 'coach', text: 'Reply', clientId: null, createdAt: '2026-01-04T09:00:01Z', replied: true },
    ];
    const c = chat(api);
    await c.load();
    expect(c.state.messages.map((m) => m.text)).toEqual(['Earlier', 'Reply']);
  });
});

describe('a reload while a message is on its way', () => {
  it('keeps it sending, with the Coach typing and not late', async () => {
    const api = fakeApi();
    let finish!: () => void;
    const c = chat(api);
    api.script = (onEvent) =>
      new Promise<void>((resolve) => {
        finish = () => {
          onEvent({ name: 'done', data: {} });
          resolve();
        };
      });

    const sending = c.send('On its way');
    const clientId = c.state.messages[0]!.clientId!;
    // Queued, not yet answered: the thread read back has it unanswered.
    api.server = [{ id: 's1', role: 'user', text: 'On its way', clientId, createdAt: '2026-01-05T09:00:00Z', replied: false }];
    await new Promise((r) => setTimeout(r, 0));
    await c.load();

    expect(c.state.messages.map((m) => m.status)).toEqual(['sending']);
    expect(c.state.typing).toBe(true);
    expect(c.state.late).toBe(false);
    finish();
    await sending;
  });
});

describe('Coach late', () => {
  afterEach(() => jest.useRealTimers());

  it('after a reload, an unanswered message is Saved again and gets its reply on resend', async () => {
    const api = fakeApi();
    api.server = [{ id: 'a', role: 'user', text: 'Hi', clientId: 'old', createdAt: '2026-01-04T09:00:00Z', replied: false }];
    const c = chat(api);
    await c.load();
    expect(c.state.messages.map((m) => m.status)).toEqual(['saved']);
    expect(c.state.late).toBe(true);

    api.script = async (emit) => {
      emit({ name: 'delta', data: { text: 'Hello' } });
      emit({ name: 'done', data: { messageId: 'm1' } });
    };
    await c.retrySaved();

    expect(api.sent).toEqual(['old']);
    expect(c.state.messages.map((m) => [m.role, m.status])).toEqual([['user', 'sent'], ['coach', 'sent']]);
    expect(c.state.late).toBe(false);
  });

  it('keeps retrying with backoff while late, and stops once the reply comes', async () => {
    jest.useFakeTimers();
    const api = fakeApi();
    api.script = async () => {
      throw new TypeError('offline');
    };
    const c = chat(api);
    await c.send('Hi');
    expect(api.sent).toHaveLength(1);

    await jest.advanceTimersByTimeAsync(5_000);
    expect(api.sent).toHaveLength(2);
    await jest.advanceTimersByTimeAsync(10_000);
    expect(api.sent).toHaveLength(3);

    api.script = async (emit) => {
      emit({ name: 'delta', data: { text: 'Back' } });
      emit({ name: 'done', data: { messageId: 'm1' } });
    };
    await jest.advanceTimersByTimeAsync(60_000);
    expect(c.state.late).toBe(false);
    expect(c.state.messages.at(-1)?.text).toBe('Back');
    const sent = api.sent.length;
    await jest.advanceTimersByTimeAsync(600_000);
    expect(api.sent).toHaveLength(sent);
  });
});

describe('day separators', () => {
  it('groups messages by local day', () => {
    const m = (id: string, createdAt: string) => ({ id, role: 'user' as const, text: id, createdAt, status: 'sent' as const });
    const groups = groupByDay([m('a', '2026-01-04T10:00:00'), m('b', '2026-01-04T11:00:00'), m('c', '2026-01-05T08:00:00')]);
    expect(groups.map((g) => [g.day, g.messages.map((x) => x.id)])).toEqual([
      ['2026-01-04', ['a', 'b']],
      ['2026-01-05', ['c']],
    ]);
  });
});
