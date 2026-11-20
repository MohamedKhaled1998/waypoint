import { proposalState } from './proposal';

const plan = (over: Partial<{ pendingVersionId: string | null; currentVersionId: string | null; currentApprovedAt: string | null }>) => ({
  pendingVersionId: null, currentVersionId: null, currentApprovedAt: null, ...over,
});

test('a proposal still waiting offers Review', () => {
  expect(proposalState('v1', [plan({ pendingVersionId: 'v1' })])).toEqual({ kind: 'waiting' });
});

test('the approved Plan says the day it was approved', () => {
  expect(proposalState('v1', [plan({ currentVersionId: 'v1', currentApprovedAt: '2026-11-20T12:00:00' })])).toEqual({ kind: 'approved', approvedOn: '2026-11-20' });
});

test('a version a later one replaced is past', () => {
  expect(proposalState('v1', [plan({ currentVersionId: 'v2' })])).toEqual({ kind: 'past' });
});

test('before the summaries load the card waits', () => {
  expect(proposalState('v1', null)).toEqual({ kind: 'waiting' });
});
