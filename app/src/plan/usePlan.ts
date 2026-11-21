import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpPlanApi } from './api';
import { offlinePlanApi } from './offlineApi';
import { createPlanReview, type PlanSummary } from './review';
import { pollPlanSummaries } from './summaryPoll';

/** One Plan review, for the version in the route. */
export function usePlanReview(versionId: string) {
  const session = useSession();
  const review = useMemo(
    () => createPlanReview(offlinePlanApi({ sync: session.phone.sync, api: httpPlanApi(session) }), versionId),
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
  const [loaded, setLoaded] = useState(false);
  // While the screen is focused, and again every 10 s while a Plan is drafting.
  const stop = useRef<(() => void) | null>(null);
  const refresh = useCallback(() => {
    stop.current?.();
    const stopThis = pollPlanSummaries(httpPlanApi(session), (p) => {
      setPlans(p);
      setLoaded(true);
    });
    stop.current = stopThis;
    // Leaving the screen stops whichever poll is current, including one a retry started since.
    return () => {
      stop.current?.();
      stop.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.status]);
  useFocusEffect(refresh);
  return { plans, loaded, refresh };
}

/** What Today shows about the Plan: drafting, late (with a retry), or which proposal waits. */
export function usePlanBanners() {
  const session = useSession();
  const { plans, refresh } = usePlanSummaries();
  const late = plans.find((p) => p.draftLate);
  return {
    plans,
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
