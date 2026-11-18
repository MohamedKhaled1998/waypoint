import { createInterview, heardStrip, type InterviewApi, type InterviewView } from './interview';
import type { SseEvent } from '../coach/sse';

const READY = {
  ready: true,
  aspiration: {
    aspiration: 'run a half marathon',
    short_name: 'Half marathon',
    short_name_suggestions: ['Half marathon', '21K'],
    picture: 'You want to run 21 km by September.',
    success_looks_like: 'finish 21 km',
    why_now: 'turning 40',
    starting_point: 'can run 3 km',
    past_attempts: [{ what: 'couch to 5k', what_worked: 'mornings', what_derailed: 'a cold' }],
    hoped_for_date: '2026-09-20',
    settle_for_picture: 'a solid 10k',
    on_skip: 'gentle_check',
    open_questions: ['which race?'],
  },
  user: { slots: [{ label: 'mornings', days: ['mon'], start: '07:00', end: '07:45' }], time_budget_hours_per_week: 5, contact_frequency: 'Gentle' },
};

type Script = (emit: (e: SseEvent) => void) => void;

function fakeApi() {
  const api = {
    view: null as InterviewView | null,
    scripts: [] as Script[],
    said: [] as { text: string; clientId: string }[],
    resumed: 0,
    confirmed: [] as string[],
    failNext: false,
    async current() {
      return api.view;
    },
    async start(ownWords: string) {
      api.view = { id: 'i1', status: 'in_progress', ownWords, isFirst: true, handOff: null, messages: [] };
      return api.view;
    },
    async resume(_id: string, emit: (e: SseEvent) => void) {
      api.resumed += 1;
      await run(emit);
    },
    async say(_id: string, text: string, clientId: string, emit: (e: SseEvent) => void) {
      api.said.push({ text, clientId });
      await run(emit);
    },
    async confirm(_id: string, shortName: string) {
      api.confirmed.push(shortName);
      return { aspirationId: 'a1', shortName, hue: 'coral' };
    },
  };
  async function run(emit: (e: SseEvent) => void) {
    if (api.failNext) {
      api.failNext = false;
      throw new Error('offline');
    }
    api.scripts.shift()?.(emit);
  }
  return api satisfies InterviewApi & Record<string, unknown>;
}

let n = 0;
const make = (api: InterviewApi) => createInterview(api, { newId: () => `c${++n}` });
const reply = (text: string, status: string, handOff: unknown = null): Script => (emit) => {
  emit({ name: 'delta', data: { text } });
  emit({ name: 'done', data: { messageId: 'm', status, handOff } });
};

describe('Interview', () => {
  it('starting asks the Coach for its first question and streams it in', async () => {
    const api = fakeApi();
    api.scripts.push(reply('What do you want?', 'in_progress', { ready: false, chips: ['A', 'B'] }));
    const iv = make(api);

    await iv.begin('run a half marathon');

    expect(iv.state.messages.map((m) => [m.role, m.text])).toEqual([['coach', 'What do you want?']]);
    expect(iv.state.chips).toEqual(['A', 'B']);
    expect(iv.state.step).toBe('chat');
  });

  it('the What I\'ve heard strip lights up as answers come in', async () => {
    const api = fakeApi();
    const iv = make(api);
    api.scripts.push(reply('Q1', 'in_progress', { ready: false, aspiration: { aspiration: 'run a half marathon' } }));
    await iv.begin('run a half marathon');
    expect(iv.state.heard.filter((h) => h.lit).map((h) => h.key)).toEqual(['aspiration']);

    api.scripts.push(
      reply('Q2', 'in_progress', {
        ready: false,
        aspiration: { aspiration: 'run a half marathon', success_looks_like: 'finish 21 km', hoped_for_date: '2026-09-20' },
      }),
    );
    await iv.send('21 km without walking');

    expect(iv.state.heard.filter((h) => h.lit).map((h) => h.key)).toEqual(['aspiration', 'success', 'dates']);
    expect(iv.state.heard.find((h) => h.key === 'success')!.text).toBe('finish 21 km');
    expect(api.said[0]).toEqual({ text: '21 km without walking', clientId: iv.state.messages.find((m) => m.role === 'user')!.clientId });
  });

  it('heardStrip names all eight parts, unlit when nothing is known', () => {
    expect(heardStrip(null).map((h) => h.key)).toEqual([
      'aspiration', 'success', 'whyNow', 'startingPoint', 'pastAttempts', 'slots', 'dates', 'coaching',
    ]);
    expect(heardStrip(null).some((h) => h.lit)).toBe(false);
  });

  it('a ready hand-off leads to the short-name step, then the picture, then confirms with the chosen name', async () => {
    const api = fakeApi();
    const iv = make(api);
    api.scripts.push(reply('Hello', 'in_progress'));
    await iv.begin('run a half marathon');
    api.scripts.push(reply('Here is what I heard.', 'awaiting_confirmation', READY));
    await iv.send('mornings');

    expect(iv.state.step).toBe('shortName');
    expect(iv.state.shortName).toBe('Half marathon');
    expect(iv.state.shortNameSuggestions).toEqual(['Half marathon', '21K']);

    expect(iv.chooseShortName('A name that is far too long')).toBe(false);
    expect(iv.state.step).toBe('shortName');
    expect(iv.chooseShortName('21K')).toBe(true);
    expect(iv.state.step).toBe('picture');
    expect(iv.state.picture).toEqual({
      text: 'You want to run 21 km by September.',
      unknowns: ['which race?'],
      hopedForDate: '2026-09-20',
      settleForPicture: 'a solid 10k',
      settleForDate: undefined,
      hoursPerWeek: undefined,
      slots: ['mornings'],
    });

    await iv.confirm();
    expect(api.confirmed).toEqual(['21K']);
    expect(iv.state.step).toBe('done');
    expect(iv.state.aspirationId).toBe('a1');
  });

  it('Correct something returns to the chat and the picture updates after the Coach answers', async () => {
    const api = fakeApi();
    const iv = make(api);
    api.scripts.push(reply('Hello', 'in_progress'));
    await iv.begin('x');
    api.scripts.push(reply('Here', 'awaiting_confirmation', READY));
    await iv.send('a');
    iv.chooseShortName('21K');

    iv.correct();
    expect(iv.state.step).toBe('chat');

    const fixed = { ...READY, aspiration: { ...READY.aspiration, picture: 'You want to run 21 km by October.' } };
    api.scripts.push(reply('Fixed', 'awaiting_confirmation', fixed));
    await iv.send('No, October');

    expect(iv.state.step).toBe('shortName');
    iv.chooseShortName('21K');
    expect(iv.state.picture!.text).toBe('You want to run 21 km by October.');
  });

  it('opening an unfinished Interview loads its transcript and asks the Coach to recap', async () => {
    const api = fakeApi();
    api.view = {
      id: 'i1', status: 'in_progress', ownWords: 'x', isFirst: true,
      handOff: { ready: false, aspiration: { why_now: 'turning 40' } },
      messages: [{ id: 'm1', role: 'coach', text: 'Why now?', clientId: null }, { id: 'm2', role: 'user', text: 'Turning 40', clientId: 'u1' }],
    };
    api.scripts.push(reply('Welcome back. Why now again?', 'in_progress'));
    const iv = make(api);

    await iv.open();

    expect(api.resumed).toBe(1);
    expect(iv.state.messages.map((m) => m.text)).toEqual(['Why now?', 'Turning 40', 'Welcome back. Why now again?']);
    expect(iv.state.heard.find((h) => h.key === 'whyNow')!.lit).toBe(true);
  });

  it('opening when there is no Interview leaves it empty', async () => {
    const iv = make(fakeApi());
    await iv.open();
    expect(iv.state.started).toBe(false);
  });

  it('a failed call keeps the answer as Saved, shows the Coach is late and fakes nothing; retry resends it', async () => {
    const api = fakeApi();
    const iv = make(api);
    api.scripts.push(reply('Hello', 'in_progress'));
    await iv.begin('x');
    api.failNext = true;

    await iv.send('my answer');

    expect(iv.state.late).toBe(true);
    expect(iv.state.messages.map((m) => [m.role, m.text, m.status])).toEqual([
      ['coach', 'Hello', 'sent'],
      ['user', 'my answer', 'saved'],
    ]);

    api.scripts.push(reply('Thanks', 'in_progress'));
    await iv.retry();

    expect(iv.state.late).toBe(false);
    expect(api.said.map((s) => s.clientId)).toEqual([api.said[0]!.clientId, api.said[0]!.clientId]);
    expect(iv.state.messages.map((m) => [m.text, m.status])).toEqual([['Hello', 'sent'], ['my answer', 'sent'], ['Thanks', 'sent']]);
  });
});
