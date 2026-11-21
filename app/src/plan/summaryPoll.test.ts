import type { PlanSummary } from './review';
import { pollPlanSummaries } from './summaryPoll';

const plan = (over: Partial<PlanSummary>): PlanSummary => ({
  aspirationId: 'a1', shortName: 'Run 16 May', hue: 'coral', drafting: false, draftLate: false,
  pendingVersionId: null, currentVersionId: null, ...over,
});
const drafting = plan({ drafting: true });
const ready = plan({ pendingVersionId: 'v1' });

/** A server that answers each check with the next summaries in line, repeating the last. */
function server(...answers: PlanSummary[][]) {
  let calls = 0;
  return {
    get calls() { return calls; },
    summaries: async () => answers[Math.min(calls++, answers.length - 1)]!,
  };
}

beforeEach(() => jest.useFakeTimers());
afterEach(() => jest.useRealTimers());

describe("Today's Plan banner while the Coach drafts", () => {
  it('checks again every 10 s while a Plan is drafting, so the proposal shows without leaving Today', async () => {
    const api = server([drafting], [ready]);
    const seen: PlanSummary[][] = [];
    pollPlanSummaries(api, (p) => seen.push(p));
    await jest.advanceTimersByTimeAsync(0);
    expect(seen).toEqual([[drafting]]);
    await jest.advanceTimersByTimeAsync(10_000);
    expect(seen).toEqual([[drafting], [ready]]);
  });

  it('stops checking once no Plan is drafting', async () => {
    const api = server([ready]);
    pollPlanSummaries(api, () => {});
    await jest.advanceTimersByTimeAsync(60_000);
    expect(api.calls).toBe(1);
  });

  it('stops checking when Today is left', async () => {
    const api = server([drafting]);
    const stop = pollPlanSummaries(api, () => {});
    await jest.advanceTimersByTimeAsync(0);
    stop();
    await jest.advanceTimersByTimeAsync(60_000);
    expect(api.calls).toBe(1);
  });

  it('keeps checking through a failed check while a Plan was drafting', async () => {
    let calls = 0;
    const answers: (() => Promise<PlanSummary[]>)[] = [async () => [drafting], async () => { throw new Error('offline'); }, async () => [ready]];
    const seen: PlanSummary[][] = [];
    pollPlanSummaries({ summaries: () => answers[Math.min(calls++, 2)]!() }, (p) => seen.push(p));
    await jest.advanceTimersByTimeAsync(20_000);
    expect(seen).toEqual([[drafting], [ready]]);
  });
});
