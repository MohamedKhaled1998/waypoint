import { createFirstRun, interviewResume, resumeDecision, stepCounter, type KeyValueStore } from './firstRun';

function memoryStore(): KeyValueStore & { data: Map<string, string> } {
  const data = new Map<string, string>();
  return {
    data,
    get: async (k) => data.get(k) ?? null,
    set: async (k, v) => void data.set(k, v),
    remove: async (k) => void data.delete(k),
  };
}

/** The phone is killed: a new first run reads what the old one persisted. */
const reopen = async (store: KeyValueStore) => {
  const f = createFirstRun(store);
  await f.load();
  return f;
};

describe('the first-run counter', () => {
  it('counts Sign in, Aspiration, Interview, First Plan as steps 1 to 4 of 4', () => {
    expect(stepCounter('signIn')).toEqual({ n: 1, of: 4 });
    expect(stepCounter('aspiration')).toEqual({ n: 2, of: 4 });
    expect(stepCounter('interview')).toEqual({ n: 3, of: 4 });
    for (const s of ['drafting', 'planReview', 'permissions', 'tourTry', 'tourWrapUp'] as const) {
      expect(stepCounter(s)).toEqual({ n: 4, of: 4 });
    }
    expect(stepCounter('done')).toBeNull();
  });
});

describe('resuming an interrupted first run', () => {
  it.each([
    ['signIn', '/sign-in'],
    ['aspiration', '/aspiration'],
    ['interview', '/interview'],
    ['drafting', '/drafting'],
    ['planReview', '/drafting'],
    ['permissions', '/'],
    ['tourTry', '/'],
    ['tourWrapUp', '/'],
  ] as const)('reopens at %s after the app is killed there', async (step, route) => {
    const store = memoryStore();
    const first = await reopen(store);
    await first.start();
    if (step !== 'signIn') await first.signedIn();
    const path = ['aspiration', 'interview', 'drafting', 'planReview', 'permissions', 'tourTry', 'tourWrapUp'] as const;
    for (const s of path) {
      if (step === 'signIn') break;
      await first.advance(s);
      if (s === step) break;
    }
    const second = await reopen(store);
    expect(second.getState().step).toBe(step);
    expect(resumeDecision(second.getState().step, null).route).toBe(route);
  });

  it('treats a signed-in phone with no stored step as finished, so existing users see no first run', async () => {
    const f = await reopen(memoryStore());
    await f.signedIn();
    expect(f.getState().step).toBe('done');
  });

  it('moves from Sign in to Aspiration once signed in', async () => {
    const store = memoryStore();
    const f = await reopen(store);
    await f.start();
    await f.signedIn();
    expect((await reopen(store)).getState().step).toBe('aspiration');
  });

  it('forgets the step on sign-out', async () => {
    const store = memoryStore();
    const f = await reopen(store);
    await f.start();
    await f.signedIn();
    await f.reset();
    expect(store.data.size).toBe(0);
  });
});

describe('the Today tour', () => {
  async function atTour() {
    const store = memoryStore();
    const f = await reopen(store);
    await f.start();
    await f.signedIn();
    await f.advance('tourTry');
    return { f, store };
  }

  it('waits for a real tap on Done on the Now card before moving on', async () => {
    const { f } = await atTour();
    await f.nowCardTapped('skipped');
    await f.nowCardTapped('partly');
    expect(f.getState().step).toBe('tourTry');
    await f.nowCardTapped('done');
    expect(f.getState().step).toBe('tourWrapUp');
  });

  it('ignores Check-in taps when the tour is not on its first step', async () => {
    const f = await reopen(memoryStore());
    await f.signedIn();
    await f.nowCardTapped('done');
    expect(f.getState().step).toBe('done');
  });

  it('finishes at the wrap-up', async () => {
    const { f, store } = await atTour();
    await f.nowCardTapped('done');
    await f.finishTour();
    expect(f.getState().step).toBe('done');
    expect((await reopen(store)).getState().step).toBe('done');
  });

  it('can be replayed from Settings, and ends back at done', async () => {
    const f = await reopen(memoryStore());
    await f.signedIn();
    await f.replayTour();
    expect(f.getState().step).toBe('tourTry');
    await f.nowCardTapped('done');
    await f.finishTour();
    expect(f.getState().step).toBe('done');
  });

  it('moves to the tour once the first Plan is approved and the permissions are settled', async () => {
    const f = await reopen(memoryStore());
    await f.start();
    await f.signedIn();
    await f.advance('planReview');
    await f.planApproved();
    expect(f.getState().step).toBe('permissions');
    await f.permissionsSettled();
    expect(f.getState().step).toBe('tourTry');
  });

  it('leaves later Plan approvals alone', async () => {
    const f = await reopen(memoryStore());
    await f.signedIn();
    await f.planApproved();
    await f.permissionsSettled();
    expect(f.getState().step).toBe('done');
  });
});

describe('what reopening does at each step', () => {
  it('goes back to the Plan review when a draft waits', () => {
    expect(resumeDecision('planReview', 'v1')).toEqual({ route: '/plan-review?id=v1', settlePermissions: false });
  });

  it('goes to the drafting wait when no draft is known yet, which opens the review when it lands', () => {
    expect(resumeDecision('planReview', null)).toEqual({ route: '/drafting', settlePermissions: false });
  });

  it('settles the permissions on reopening at that step, so the tour is reached', () => {
    expect(resumeDecision('permissions', null)).toEqual({ route: '/', settlePermissions: true });
  });

  it('does nothing for a finished first run', () => {
    expect(resumeDecision('done', null)).toEqual({ route: null, settlePermissions: false });
    expect(resumeDecision(null, null)).toEqual({ route: null, settlePermissions: false });
  });

  it('sends the Interview step back to the Aspiration step when no Interview exists and no words came with it', () => {
    expect(interviewResume({ inFirstRun: true, opened: true, started: false, hasWords: false })).toBe('aspiration');
  });

  it.each([
    ['an Interview was found to resume', { inFirstRun: true, opened: true, started: true, hasWords: false }],
    ['the words just came from the Aspiration step', { inFirstRun: true, opened: true, started: false, hasWords: true }],
    ['the server has not been asked yet', { inFirstRun: true, opened: false, started: false, hasWords: false }],
    ['it is not first run', { inFirstRun: false, opened: true, started: false, hasWords: false }],
  ])('stays on the Interview when %s', (_, input) => {
    expect(interviewResume(input)).toBe('stay');
  });
});
