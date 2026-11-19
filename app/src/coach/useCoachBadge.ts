import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { useSession } from '../auth/SessionProvider';
import { httpPlanApi } from '../plan/api';
import { phoneStorage } from '../sync/phone';
import type { StoredMessage } from './chat';
import { tabBadge, type ThreadAspiration } from './threads';
import { useLastReadAt } from './unread';

/** The Coach thread's aspirations with their waiting proposals, read from the server when the screen is focused. */
export function useThreadAspirations() {
  const session = useSession();
  const [aspirations, setAspirations] = useState<ThreadAspiration[]>([]);
  const refresh = useCallback(() => {
    httpPlanApi(session)
      .summaries()
      .then((plans) => setAspirations(plans.map((p) => ({ aspirationId: p.aspirationId, shortName: p.shortName, hue: p.hue, pendingVersionId: p.pendingVersionId }))))
      .catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.status]);
  useFocusEffect(refresh);
  return aspirations;
}

/** The Coach tab's badge: unread Coach messages plus proposals waiting for Review. */
export function useCoachBadge(): number | undefined {
  const session = useSession();
  const aspirations = useThreadAspirations();
  const lastReadAt = useLastReadAt();
  const [messages, setMessages] = useState<StoredMessage[]>([]);
  useEffect(() => {
    const read = () => void phoneStorage.list<StoredMessage>('chat_message').then(setMessages).catch(() => {});
    read();
    return session.phone.subscribe(read);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.status]);
  const count = tabBadge(
    messages.map((m) => ({ id: m.id, role: m.role, text: m.text, createdAt: m.createdAt, status: 'sent' as const })),
    lastReadAt,
    aspirations,
  );
  return count > 0 ? count : undefined;
}
