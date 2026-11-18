export type PlanTab = 'route' | 'twoWeeks' | 'habits' | 'assumes';

/** The first Plan's tabs, in order. */
export const PLAN_TABS: PlanTab[] = ['route', 'twoWeeks', 'habits', 'assumes'];

export type PlanVersionView = {
  id: string;
  aspirationId: string;
  shortName: string;
  hue: 'coral' | 'teal' | 'amber';
  number: number | null;
  status: 'proposed' | 'approved' | 'replaced' | 'superseded';
  reason: string;
  rationale: string;
  ramp: string;
  assumptions: string[];
  startDate: string;
  milestones: { id: string; date: string; title: string; reachedWhen: string; kind: 'effort' | 'ability'; state: string }[];
  habits: { id: string; title: string; timesPerWeek: number; minutes: number; slots: string[] }[];
  tasks: { id: string; title: string; minutes: number; due: string | null; milestone: string | null }[];
  twoWeeks: { date: string; slot: string; action: string; kind: 'habit' | 'task'; minutes: number }[];
};

/** The tab the screen opens on. It is on screen from the start, so it counts as looked at and has no dot. */
export const LANDING_TAB: PlanTab = 'route';

/** "Talk it through" leaves the review for the Coach chat. */
export const TALK_IT_THROUGH_ROUTE = '/coach';

export type PlanSummary = {
  aspirationId: string;
  shortName: string;
  hue: string;
  drafting: boolean;
  /** The Coach gave up drafting: say the Plan is late and offer a retry. */
  draftLate: boolean;
  pendingVersionId: string | null;
  currentVersionId: string | null;
};

export type ApproveResult =
  | { kind: 'approved'; version: PlanVersionView }
  | { kind: 'stale'; newer: PlanVersionView | null; reason?: 'replaced' | 'window_started' }
  /** Kept on the phone to send when the server is back. */
  | { kind: 'queued' };

export interface PlanApi {
  get(id: string): Promise<PlanVersionView>;
  /** A version the server has already replaced comes back as `stale`, with the newer one. */
  approve(id: string): Promise<ApproveResult>;
}

export type PlanReviewState = {
  version: PlanVersionView | null;
  /** Tabs not yet visited, in tab order: each gets a dot. */
  unopened: PlanTab[];
  approving: boolean;
  outcome: null | 'approved' | 'queued' | 'replaced' | 'redrafting' | 'failed';
};

/** The tabs to name in the "Start this Plan?" confirm. */
export const unopenedNames = (unopened: PlanTab[]): PlanTab[] => PLAN_TABS.filter((t) => unopened.includes(t));

/** What the "Start this Plan?" confirm shows: the unopened tabs to name, and the one its "Look at …" button opens. */
export function confirmModel(unopened: PlanTab[]): { unopenedTabs: PlanTab[]; lookAt: PlanTab | null } {
  const unopenedTabs = unopenedNames(unopened);
  return { unopenedTabs, lookAt: unopenedTabs[0] ?? null };
}

/** The screen's tab bar: every first-Plan tab in order, with a dot while it is unopened. */
export const tabBar = (unopened: PlanTab[]): { tab: PlanTab; dot: boolean }[] =>
  PLAN_TABS.map((tab) => ({ tab, dot: unopened.includes(tab) }));

/** State of one Plan review: which tabs were looked at, and what approving did. */
export function createPlanReview(api: PlanApi, versionId: string) {
  const listeners = new Set<() => void>();
  let opened = new Set<PlanTab>([LANDING_TAB]);
  const review = {
    state: { version: null, unopened: [], approving: false, outcome: null } as PlanReviewState,
    subscribe(fn: () => void) {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    },
    getState: () => review.state,

    async load() {
      set({ version: await api.get(versionId) });
    },

    /** The tab on screen counts as looked at. */
    open(tab: PlanTab) {
      opened = new Set(opened).add(tab);
      set({});
    },

    async approve() {
      const current = review.state.version;
      if (!current || review.state.approving) return;
      set({ approving: true, outcome: null });
      try {
        const result = await api.approve(current.id);
        if (result.kind === 'approved') set({ version: result.version, approving: false, outcome: 'approved' });
        else if (result.kind === 'queued') set({ approving: false, outcome: 'queued' });
        else if (!result.newer) {
          // Its two weeks had started, so the server is drafting a fresh Plan: nothing to review until it arrives.
          set({ approving: false, outcome: 'redrafting' });
        } else {
          // The server replaced this version: show the newer one, unread.
          opened = new Set<PlanTab>([LANDING_TAB]);
          set({ version: result.newer, approving: false, outcome: 'replaced' });
        }
      } catch {
        set({ approving: false, outcome: 'failed' });
      }
    },
  };

  function set(patch: Partial<PlanReviewState>) {
    const next = { ...review.state, ...patch };
    review.state = { ...next, unopened: PLAN_TABS.filter((t) => !opened.has(t)) };
    listeners.forEach((fn) => fn());
  }
  set({});
  return review;
}
