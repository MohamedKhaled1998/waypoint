import type { PauseScope, PauseUntil, PauseView } from '../pause/pause';
import type { PlanSummary } from '../plan/review';
import type { ChatMessage } from './chat';

/** What a Pause suggestion card holds, as the server writes it. */
export type PauseCardData = {
  clientId: string;
  aspirationId: string | null;
  span: 'today' | 'until' | 'open';
  /** The last paused day, for `until`. */
  endsOn: string | null;
  shortName: string | null;
};

/** What one tap on a Pause suggestion starts: the scope and end the Coach read, under the card's own client ID. */
export function pauseFromCard(card: PauseCardData): { scope: PauseScope; until: PauseUntil; clientId: string } {
  const until: PauseUntil = card.span === 'open' ? 'open' : card.span === 'until' && card.endsOn ? { on: card.endsOn } : 'today';
  return { scope: card.aspirationId ? { aspirationId: card.aspirationId } : {}, until, clientId: card.clientId };
}

export type Waiting =
  | { kind: 'proposal'; key: string; versionId: string; shortName: string; hue: string; reason: string }
  | { kind: 'pause'; key: string; card: PauseCardData };

const DAY_MS = 86_400_000;

/**
 * What waits on the user, for the pinned bar: Plan proposals still pending on the server, and Pause suggestions not
 * started yet (not for ever: a suggestion fades once its last day has passed or it is a day old).
 */
export function waitingItems(messages: ChatMessage[], plans: PlanSummary[], pauses: PauseView[], now: Date): Waiting[] {
  const pending = new Set(plans.map((p) => p.pendingVersionId).filter((id): id is string => !!id));
  const started = new Set(pauses.map((p) => p.clientId));
  const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  const out: Waiting[] = [];
  for (const m of messages) {
    const data = m.cardData;
    if (m.card === 'proposal' && data?.planVersionId && pending.has(String(data.planVersionId))) {
      out.push({ kind: 'proposal', key: m.id, versionId: String(data.planVersionId), shortName: data.shortName, hue: data.hue, reason: data.reason ?? '' });
    } else if (m.card === 'pause_suggestion' && data?.clientId) {
      const card = data as PauseCardData;
      const fresh = now.getTime() - new Date(m.createdAt).getTime() < DAY_MS;
      if (!started.has(card.clientId) && fresh && !(card.endsOn && card.endsOn < today)) out.push({ kind: 'pause', key: m.id, card });
    }
  }
  return out;
}
