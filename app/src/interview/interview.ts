import type { SseEvent } from '../coach/sse';

export const SHORT_NAME_MAX = 16;

type Json = Record<string, any>;
export type HandOff = Json;

export type InterviewView = {
  id: string;
  status: 'in_progress' | 'awaiting_confirmation' | 'confirmed';
  ownWords: string;
  isFirst: boolean;
  handOff: HandOff | null;
  messages: { id: string; role: 'user' | 'coach'; text: string; clientId: string | null }[];
};

export interface InterviewApi {
  current(): Promise<InterviewView | null>;
  start(ownWords: string): Promise<InterviewView>;
  /** Streams the Coach's opening question, or its recap and last question. */
  resume(id: string, onEvent: (e: SseEvent) => void): Promise<void>;
  say(id: string, text: string, clientId: string, onEvent: (e: SseEvent) => void): Promise<void>;
  confirm(id: string, shortName: string): Promise<{ aspirationId: string; shortName: string; hue: string }>;
}

export type HeardKey =
  | 'aspiration' | 'success' | 'whyNow' | 'startingPoint' | 'pastAttempts' | 'slots' | 'dates' | 'coaching';
export type Heard = { key: HeardKey; lit: boolean; text: string };

/** The "What I've heard" strip: eight parts, lit once the Coach has the answer. */
export function heardStrip(handOff: HandOff | null): Heard[] {
  const a: Json = handOff?.aspiration ?? {};
  const slots: string[] = (handOff?.user?.slots ?? []).map((s: Json) => s.label).filter(Boolean);
  const attempts: string[] = (a.past_attempts ?? []).map((p: Json) => p.what).filter(Boolean);
  const dates = [a.hoped_for_date, a.settle_for_date].filter(Boolean).join(' · ');
  const part = (key: HeardKey, text: string | undefined): Heard => ({ key, lit: !!text, text: text ?? '' });
  return [
    part('aspiration', a.aspiration),
    part('success', a.success_looks_like),
    part('whyNow', a.why_now),
    part('startingPoint', a.starting_point),
    part('pastAttempts', attempts.join(', ')),
    part('slots', slots.join(', ')),
    part('dates', dates),
    part('coaching', a.on_skip),
  ];
}

export type Picture = {
  text: string;
  unknowns: string[];
  hopedForDate?: string;
  settleForDate?: string;
  settleForPicture?: string;
  hoursPerWeek?: number;
  slots: string[];
};

export type InterviewMessage = {
  id: string;
  clientId?: string;
  role: 'user' | 'coach';
  text: string;
  status: 'sending' | 'saved' | 'sent';
};

export type InterviewState = {
  started: boolean;
  id?: string;
  status: InterviewView['status'];
  messages: InterviewMessage[];
  typing: boolean;
  late: boolean;
  chips: string[];
  heard: Heard[];
  /** chat, then once the picture is ready: shortName, picture; done after confirming. */
  step: 'chat' | 'shortName' | 'picture' | 'done';
  shortName: string;
  shortNameSuggestions: string[];
  picture?: Picture;
  aspirationId?: string;
};

type Deps = { newId: () => string };

export function createInterview(api: InterviewApi, deps: Deps) {
  const listeners = new Set<() => void>();
  let handOff: HandOff | null = null;
  let needsResume = false;
  let queue: Promise<unknown> = Promise.resolve();

  const iv = {
    state: {
      started: false, status: 'in_progress', messages: [], typing: false, late: false, chips: [],
      heard: heardStrip(null), step: 'chat', shortName: '', shortNameSuggestions: [],
    } as InterviewState,
    subscribe(fn: () => void) {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    getState: () => iv.state,

    /** A new Interview for the user's own words; the Coach asks its first question. */
    async begin(ownWords: string) {
      load(await api.start(ownWords));
      await enqueue(streamResume);
    },

    /** Picks an unfinished Interview back up: the transcript is the server's, the Coach recaps. */
    async open() {
      const view = await api.current();
      if (!view) return;
      load(view);
      await enqueue(streamResume);
    },

    send(text: string) {
      const clientId = deps.newId();
      set({
        chips: [],
        messages: [...iv.state.messages, { id: clientId, clientId, role: 'user', text, status: 'sending' }],
      });
      return enqueue(() => deliver(clientId));
    },

    /** Resends what is Saved (same client IDs) and asks again for a reply the Coach owes. */
    retry() {
      return enqueue(async () => {
        if (needsResume) await streamResume();
        for (const m of iv.state.messages.filter((x) => x.role === 'user' && x.status === 'saved'))
          await deliver(m.clientId!);
      });
    },

    /** Returns false (and stays on the step) when the short name is empty or over 16 characters. */
    chooseShortName(name: string) {
      const trimmed = name.trim();
      if (!trimmed || trimmed.length > SHORT_NAME_MAX) return false;
      set({ shortName: trimmed, step: 'picture', picture: pictureOf(handOff) });
      return true;
    },

    /** "Correct something": back into the chat; the picture updates when the Coach answers. */
    correct() {
      set({ step: 'chat' });
    },

    async confirm() {
      const result = await api.confirm(iv.state.id!, iv.state.shortName);
      set({ step: 'done', status: 'confirmed', aspirationId: result.aspirationId });
    },
  };

  function set(patch: Partial<InterviewState>) {
    iv.state = { ...iv.state, ...patch };
    listeners.forEach((fn) => fn());
  }

  function enqueue(job: () => Promise<void>) {
    const run = queue.then(job);
    queue = run.catch(() => {});
    return run;
  }

  function load(view: InterviewView) {
    handOff = view.handOff;
    set({
      started: true,
      id: view.id,
      status: view.status,
      messages: view.messages.map((m) => ({
        id: m.id, clientId: m.clientId ?? undefined, role: m.role, text: m.text, status: 'sent' as const,
      })),
      heard: heardStrip(handOff),
      step: 'chat',
    });
  }

  /** Runs one streamed Coach turn; resolves true when the Coach answered. */
  async function stream(run: (emit: (e: SseEvent) => void) => Promise<void>) {
    const replyId = `reply-${deps.newId()}`;
    let done = false;
    let failed = false;
    set({ typing: true });
    const emit = (e: SseEvent) => {
      if (e.name === 'delta') {
        const exists = iv.state.messages.some((m) => m.id === replyId);
        set({
          typing: false,
          messages: exists
            ? iv.state.messages.map((m) => (m.id === replyId ? { ...m, text: m.text + e.data.text } : m))
            : [...iv.state.messages, { id: replyId, role: 'coach', text: e.data.text, status: 'sending' }],
        });
      } else if (e.name === 'done') {
        done = true;
        applyDone(replyId, e.data);
      } else if (e.name === 'error') {
        failed = true;
      }
    };
    try {
      await run(emit);
    } catch {
      failed = true;
    }
    if (failed || !done) {
      // Late, never faked: drop a half reply, keep what the user said.
      set({ messages: iv.state.messages.filter((m) => m.id !== replyId), typing: false, late: true });
      return false;
    }
    set({ typing: false, late: false });
    return true;
  }

  function applyDone(replyId: string, data: Json) {
    if (data.handOff) handOff = data.handOff;
    const status: InterviewView['status'] = data.status ?? iv.state.status;
    const ready = status === 'awaiting_confirmation';
    set({
      status,
      messages: iv.state.messages.map((m) => (m.id === replyId || m.role === 'user' ? { ...m, status: 'sent' as const } : m)),
      chips: ready ? [] : Array.isArray(handOff?.chips) ? handOff!.chips.filter((c: unknown) => typeof c === 'string') : [],
      heard: heardStrip(handOff),
      ...(ready
        ? {
            step: 'shortName' as const,
            shortName: handOff?.aspiration?.short_name ?? '',
            shortNameSuggestions: handOff?.aspiration?.short_name_suggestions ?? [],
          }
        : {}),
    });
  }

  async function streamResume() {
    const ok = await stream((emit) => api.resume(iv.state.id!, emit));
    needsResume = !ok;
  }

  async function deliver(clientId: string) {
    const mine = iv.state.messages.find((m) => m.clientId === clientId)!;
    const ok = await stream((emit) => api.say(iv.state.id!, mine.text, clientId, emit));
    set({
      messages: iv.state.messages.map((m) => (m.clientId === clientId ? { ...m, status: ok ? 'sent' : 'saved' } : m)),
    });
  }

  return iv;
}

function pictureOf(handOff: HandOff | null): Picture {
  const a: Json = handOff?.aspiration ?? {};
  return {
    text: a.picture ?? '',
    unknowns: a.open_questions ?? [],
    hopedForDate: a.hoped_for_date,
    settleForDate: a.settle_for_date,
    settleForPicture: a.settle_for_picture,
    hoursPerWeek: a.share_hours_per_week,
    slots: (handOff?.user?.slots ?? []).map((s: Json) => s.label),
  };
}

export type Interview = ReturnType<typeof createInterview>;
