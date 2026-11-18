import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpPlanApi } from './api';
import { createPlanReview, type PlanSummary } from './review';

/** One Plan review, for the version in the route. */
export function usePlanReview(versionId: string) {
  const session = useSession();
  const review = useMemo(
    () => createPlanReview(httpPlanApi(session), versionId),
    // Token functions are read at call time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.status, versionId],
  );
  const state = useSyncExternalStore(review.subscribe, review.getState);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    review.load().catch(() => setFailed(true));
  }, [review]);
  return { ...state, loadFailed: failed, open: review.open, approve: review.approve };
}

/** Which Aspirations have a Plan being drafted, late or waiting for approval (Today's quiet banner). */
export function usePlanSummaries() {
  const session = useSession();
  const [plans, setPlans] = useState<PlanSummary[]>([]);
  const refresh = useCallback(() => {
    httpPlanApi(session)
      .summaries()
      .then(setPlans)
      .catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.status]);
  useFocusEffect(refresh);
  return { plans, refresh };
}

/** What Today shows about the Plan: drafting, late (with a retry), or which proposal waits. */
export function usePlanBanners() {
  const session = useSession();
  const { plans, refresh } = usePlanSummaries();
  const late = plans.find((p) => p.draftLate);
  return {
    drafting: plans.some((p) => p.drafting),
    late: late !== undefined,
    retryDraft: async () => {
      if (!late) return;
      await httpPlanApi(session).retryDraft(late.aspirationId).catch(() => {});
      refresh();
    },
    waitingVersionId: plans.find((p) => p.pendingVersionId)?.pendingVersionId ?? null,
  };
}
