import {
  LANDING_TAB,
  PLAN_TABS,
  TALK_IT_THROUGH_ROUTE,
  confirmModel,
  createPlanReview,
  tabBar,
  type PlanApi,
  type PlanVersionView,
} from './review';

const version = (id: string, over: Partial<PlanVersionView> = {}): PlanVersionView => ({
  id,
  aspirationId: 'a1',
  shortName: 'Half marathon',
  hue: 'coral',
  number: null,
  status: 'proposed',
  reason: 'first_plan',
  rationale: 'Short sessions before work.',
  ramp: 'One more session when it goes well.',
  assumptions: ['You can run on Wednesdays.'],
  startDate: '2026-01-06',
  milestones: [],
  habits: [],
  tasks: [],
  twoWeeks: [],
  ...over,
});

function fakeApi(over: Partial<PlanApi> = {}) {
  const calls: string[] = [];
  const api: PlanApi = {
    async get(id) {
      calls.push(`get ${id}`);
      return version(id);
    },
    async approve(id) {
      calls.push(`approve ${id}`);
      return { kind: 'approved', version: version(id, { status: 'approved', number: 1 }) };
    },
    ...over,
  };
  return { api, calls };
}

describe('Plan review', () => {
  it('marks every tab but the one on screen as unopened, and a visit clears its dot', async () => {
    const review = createPlanReview(fakeApi().api, 'v1');
    await review.load();
    expect(PLAN_TABS).toEqual(['route', 'twoWeeks', 'habits', 'assumes']);
    expect(review.getState().unopened).toEqual(['twoWeeks', 'habits', 'assumes']);

    review.open('habits');
    expect(review.getState().unopened).toEqual(['twoWeeks', 'assumes']);
  });

  it('shows the four first-Plan tabs in order, a dot on each one not yet looked at', async () => {
    const review = createPlanReview(fakeApi().api, 'v1');
    await review.load();
    expect(LANDING_TAB).toBe('route'); // on screen from the start, so it counts as looked at
    expect(tabBar(review.getState().unopened)).toEqual([
      { tab: 'route', dot: false },
      { tab: 'twoWeeks', dot: true },
      { tab: 'habits', dot: true },
      { tab: 'assumes', dot: true },
    ]);
    review.open('twoWeeks');
    expect(tabBar(review.getState().unopened).map((t) => t.dot)).toEqual([false, false, true, true]);
  });

  it('"Start this Plan?" names the unopened tabs and its "Look at" button opens the first of them', async () => {
    const review = createPlanReview(fakeApi().api, 'v1');
    await review.load();
    review.open('twoWeeks');
    expect(confirmModel(review.getState().unopened)).toEqual({ unopenedTabs: ['habits', 'assumes'], lookAt: 'habits' });

    review.open(confirmModel(review.getState().unopened).lookAt!);
    expect(confirmModel(review.getState().unopened)).toEqual({ unopenedTabs: ['assumes'], lookAt: 'assumes' });

    review.open('assumes');
    expect(confirmModel(review.getState().unopened)).toEqual({ unopenedTabs: [], lookAt: null });
  });

  it('Talk it through goes back to the Coach chat', () => {
    expect(TALK_IT_THROUGH_ROUTE).toBe('/coach');
  });

  it('says the Plan is being drafted afresh when the server rejects a version whose days have started', async () => {
    const { api } = fakeApi({
      async approve() {
        return { kind: 'stale', newer: null, reason: 'window_started' };
      },
    });
    const review = createPlanReview(api, 'v1');
    await review.load();
    await review.approve();
    expect(review.getState().outcome).toBe('redrafting');
    expect(review.getState().approving).toBe(false);
  });

  it('approving makes it the current version and reports it', async () => {
    const { api, calls } = fakeApi();
    const review = createPlanReview(api, 'v1');
    await review.load();
    await review.approve();
    expect(calls).toContain('approve v1');
    expect(review.getState().outcome).toBe('approved');
    expect(review.getState().version?.number).toBe(1);
  });

  it('shows the newer version when the server has replaced the one being reviewed, with every tab unopened again', async () => {
    const { api } = fakeApi({
      async approve() {
        return { kind: 'stale', newer: version('v2') };
      },
    });
    const review = createPlanReview(api, 'v1');
    await review.load();
    review.open('habits');
    await review.approve();
    const s = review.getState();
    expect(s.outcome).toBe('replaced');
    expect(s.version?.id).toBe('v2');
    expect(s.unopened).toEqual(['twoWeeks', 'habits', 'assumes']);
  });

  it('keeps the proposal and says so when approving cannot reach the server', async () => {
    const { api } = fakeApi({
      async approve() {
        throw new Error('offline');
      },
    });
    const review = createPlanReview(api, 'v1');
    await review.load();
    await review.approve();
    expect(review.getState().outcome).toBe('failed');
    expect(review.getState().version?.id).toBe('v1');
  });
});
