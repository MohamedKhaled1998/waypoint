import type { SseEvent } from './sse';

export type StoredMessage = {
  id: string;
  role: 'user' | 'coach';
  text: string;
  clientId: string | null; createdAt: string;
  /** The Aspiration the message is about, if any. */
  aspirationId?: string | null;
  /** Set on a card message, e.g. "proposal". */
  card?: string | null;
  cardData?: Record<string, any> | null;
  /** False for a user message the Coach has not answered yet (kept on the server, waiting for a successful call). */
  replied: boolean;
};

/** `sending`: in flight. `saved`: kept on the phone, the Coach will reply when it can. `sent`: the server has it. */
export type ChatMessage = {
  id: string;
  clientId?: string;
  role: 'user' | 'coach';
  text: string;
  createdAt: string;
  status: 'sending' | 'saved' | 'sent';
  aspirationId?: string;
  card?: string;
  cardData?: Record<string, any>;
};

export type ChatState = { messages: ChatMessage[]; typing: boolean; late: boolean };

export interface CoachApi {
  history(): Promise<StoredMessage[]>;
  /** Resolves when the stream ends; rejects when the server cannot be reached. */
  send(text: string, clientId: string, onEvent: (e: SseEvent) => void, aspirationId?: string): Promise<void>;
}

type Deps = { newId: () => string; now: () => string; retryDelaysMs?: number[] };

const DEFAULT_RETRY_DELAYS_MS = [5_000, 10_000, 30_000, 60_000];

export function createCoachChat(api: CoachApi, deps: Deps) {
  const listeners = new Set<() => void>();
  let queue: Promise<unknown> = Promise.resolve();
  let timer: ReturnType<typeof setTimeout> | null = null;
  let attempt = 0;
  const delays = deps.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
  const chat = {
    state: { messages: [], typing: false, late: false } as ChatState,
    subscribe(fn: () => void) {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    getState: () => chat.state,

    /** Stops the retry timer (the screen is gone). */
    dispose() {
      if (timer) clearTimeout(timer);
      timer = null;
    },

    async load() {
      const stored = await api.history();
      const known = new Set(stored.map((m) => m.clientId));
      const local = chat.state.messages.filter((m) => m.clientId && m.status !== 'sent' && !known.has(m.clientId));
      set({
        messages: [
          ...stored.map(
            (m): ChatMessage => ({
              id: m.id,
              role: m.role,
              text: m.text,
              createdAt: m.createdAt,
              clientId: m.clientId ?? undefined,
              aspirationId: m.aspirationId ?? undefined,
              card: m.card ?? undefined,
              cardData: m.cardData ?? undefined,
              // Stored but unanswered: still Saved, and resent with its client ID.
              status: m.role === 'user' && !m.replied ? 'saved' : 'sent',
            }),
          ),
          ...local,
        ],
      });
      // The Coach is late while a message waits for its reply, and not once the thread shows every one answered.
      set({ late: chat.state.messages.some((m) => m.status === 'saved') });
    },

    /** Adds the message at once, then delivers it. Deliveries run one at a time, in order. */
    send(text: string, aspirationId?: string): Promise<void> {
      const clientId = deps.newId();
      set({
        messages: [
          ...chat.state.messages,
          { id: clientId, clientId, role: 'user', text, createdAt: deps.now(), status: 'sending', aspirationId },
        ],
      });
      return enqueue(() => deliver(clientId));
    },

    /** Resends every Saved message (same client IDs, so the server never duplicates). */
    retrySaved(): Promise<void> {
      const ids = chat.state.messages.filter((m) => m.role === 'user' && m.status === 'saved').map((m) => m.clientId!);
      return enqueue(async () => {
        for (const id of ids) await deliver(id);
      });
    },
  };

  function set(patch: Partial<ChatState>) {
    chat.state = { ...chat.state, ...patch };
    listeners.forEach((fn) => fn());
  }

  function update(id: string, fn: (m: ChatMessage) => ChatMessage) {
    set({ messages: chat.state.messages.map((m) => (m.id === id ? fn(m) : m)) });
  }

  /** While the Coach is late, resend Saved messages on a backoff until a call succeeds. */
  function scheduleRetry() {
    if (timer || !chat.state.messages.some((m) => m.status === 'saved')) return;
    const delay = delays[Math.min(attempt, delays.length - 1)]!;
    attempt += 1;
    timer = setTimeout(() => {
      timer = null;
      void chat.retrySaved();
    }, delay);
  }

  function enqueue(job: () => Promise<void>): Promise<void> {
    const run = queue.then(job);
    queue = run.catch(() => {});
    return run;
  }

  async function deliver(clientId: string) {
    const mine = chat.state.messages.find((m) => m.clientId === clientId)!;
    update(mine.id, (m) => ({ ...m, status: 'sending' }));
    set({ typing: true });
    const replyId = `reply-${clientId}`;
    let failed = false;
    let done = false;
    const onEvent = (e: SseEvent) => {
      if (e.name === 'delta') {
        const exists = chat.state.messages.some((m) => m.id === replyId);
        set({
          typing: false,
          messages: exists
            ? chat.state.messages.map((m) => (m.id === replyId ? { ...m, text: m.text + e.data.text } : m))
            : [
                ...chat.state.messages,
                { id: replyId, role: 'coach', text: e.data.text, createdAt: deps.now(), status: 'sending' },
              ],
        });
      } else if (e.name === 'done') {
        done = true;
        update(mine.id, (m) => ({ ...m, status: 'sent' }));
        update(replyId, (m) => ({ ...m, status: 'sent' }));
      } else if (e.name === 'error') {
        failed = true;
      }
    };
    try {
      await api.send(mine.text, clientId, onEvent, mine.aspirationId);
    } catch {
      failed = true;
    }
    if (failed || !done) {
      // Never fake a Coach line: drop any half reply, keep the user's message.
      set({ messages: chat.state.messages.filter((m) => m.id !== replyId), typing: false, late: true });
      update(mine.id, (m) => ({ ...m, status: 'saved' }));
      scheduleRetry();
    } else {
      attempt = 0;
      set({ typing: false, late: false });
    }
  }

  return chat;
}

export type CoachChat = ReturnType<typeof createCoachChat>;

/** Messages grouped under their local calendar day (YYYY-MM-DD), for day separators. */
export function groupByDay(messages: ChatMessage[]) {
  const groups: { day: string; messages: ChatMessage[] }[] = [];
  for (const m of messages) {
    const day = localDay(new Date(m.createdAt));
    const last = groups.at(-1);
    if (last?.day === day) last.messages.push(m);
    else groups.push({ day, messages: [m] });
  }
  return groups;
}

function localDay(d: Date) {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}
