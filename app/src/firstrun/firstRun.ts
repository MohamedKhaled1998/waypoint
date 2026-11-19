/** The first-run steps, in order. `done` is also what a phone that signed in before first run existed reads as. */
export type FirstRunStep =
  | 'signIn'
  | 'aspiration'
  | 'interview'
  | 'drafting'
  | 'planReview'
  | 'permissions'
  | 'tourTry'
  | 'tourWrapUp'
  | 'done';

export interface KeyValueStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  remove(key: string): Promise<void>;
}

export const FIRST_RUN_KEY = 'first-run-step';
const STEPS: FirstRunStep[] = ['signIn', 'aspiration', 'interview', 'drafting', 'planReview', 'permissions', 'tourTry', 'tourWrapUp', 'done'];

/** "Step n of 4": Sign in → Aspiration → Interview → First Plan (the Plan, its permissions and the tour). */
export function stepCounter(step: FirstRunStep): { n: number; of: 4 } | null {
  if (step === 'done') return null;
  if (step === 'signIn') return { n: 1, of: 4 };
  if (step === 'aspiration') return { n: 2, of: 4 };
  if (step === 'interview') return { n: 3, of: 4 };
  return { n: 4, of: 4 };
}

export type FirstRunState = { loaded: boolean; step: FirstRunStep | null };

/**
 * Where the first run is, kept on the phone after every move so a killed app reopens in the step it left. No stored
 * step on a signed-in phone means first run never applied (or is over): `done`.
 */
export function createFirstRun(store: KeyValueStore) {
  let state: FirstRunState = { loaded: false, step: null };
  const listeners = new Set<() => void>();
  const set = async (step: FirstRunStep | null) => {
    state = { loaded: true, step };
    if (step) await store.set(FIRST_RUN_KEY, step);
    else await store.remove(FIRST_RUN_KEY);
    listeners.forEach((l) => l());
  };
  return {
    getState: () => state,
    subscribe(l: () => void) {
      listeners.add(l);
      return () => void listeners.delete(l);
    },
    async load() {
      const stored = await store.get(FIRST_RUN_KEY);
      state = { loaded: true, step: STEPS.includes(stored as FirstRunStep) ? (stored as FirstRunStep) : null };
      listeners.forEach((l) => l());
    },
    /** A signed-out phone showing the cards: the first run has begun. */
    async start() {
      if (state.step === null) await set('signIn');
    },
    /** Signed in: first run moves past Sign in; a phone with no first run in progress is simply done. */
    async signedIn() {
      if (state.step === 'signIn') await set('aspiration');
      else if (state.step === null) await set('done');
    },
    advance: (step: FirstRunStep) => set(step),
    /** Sign-out: nothing of this user's first run stays. */
    reset: () => set(null),
    async planApproved() {
      if (state.step === 'drafting' || state.step === 'planReview') await set('permissions');
    },
    async permissionsSettled() {
      if (state.step === 'permissions') await set('tourTry');
    },
    /** Only a real ✓ on the Now card moves the tour on; Skip and Partly do not. */
    async nowCardTapped(result: 'done' | 'partly' | 'skipped') {
      if (state.step === 'tourTry' && result === 'done') await set('tourWrapUp');
    },
    async finishTour() {
      if (state.step === 'tourWrapUp' || state.step === 'tourTry') await set('done');
    },
    replayTour: () => set('tourTry'),
  };
}

/**
 * What reopening the app does at a step: the screen to go back to, and whether the permissions step still has to be
 * run (it was cut off, so it is raised again and then the tour begins). The Plan review needs the waiting version's
 * id; without one, the drafting wait opens the review the moment the draft lands.
 */
export function resumeDecision(step: FirstRunStep | null, planVersionId: string | null): { route: string | null; settlePermissions: boolean } {
  switch (step) {
    case 'signIn': return { route: '/sign-in', settlePermissions: false };
    case 'aspiration': return { route: '/aspiration', settlePermissions: false };
    case 'interview': return { route: '/interview', settlePermissions: false };
    case 'drafting': return { route: '/drafting', settlePermissions: false };
    case 'planReview': return { route: planVersionId ? `/plan-review?id=${planVersionId}` : '/drafting', settlePermissions: false };
    case 'permissions': return { route: '/', settlePermissions: true };
    case 'tourTry':
    case 'tourWrapUp': return { route: '/', settlePermissions: false };
    default: return { route: null, settlePermissions: false };
  }
}

/** The Interview step with no Interview on the server and no words to start one repeats the Aspiration step. */
export function interviewResume(i: { inFirstRun: boolean; opened: boolean; started: boolean; hasWords: boolean }): 'aspiration' | 'stay' {
  return i.inFirstRun && i.opened && !i.started && !i.hasWords ? 'aspiration' : 'stay';
}

export type FirstRun = ReturnType<typeof createFirstRun>;
