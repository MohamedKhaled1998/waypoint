import type { Hue } from '../theme/tokens';
import type { ChatMessage } from './chat';

/** What the thread needs to know about each active Aspiration. */
export type ThreadAspiration = { aspirationId: string; shortName: string; hue: string; pendingVersionId: string | null };

const CHIP_MAX = 15;

const asHue = (hue: string): Hue | undefined => (hue === 'coral' || hue === 'teal' || hue === 'amber' ? hue : undefined);

/** The thread, or one Aspiration's part of it. */
export function filterThread(messages: ChatMessage[], aspirationId: string | null): ChatMessage[] {
  return aspirationId === null ? messages : messages.filter((m) => m.aspirationId === aspirationId);
}

/** The tag a message carries: the Aspiration's short name and hue. */
export function tagOf(message: ChatMessage, aspirations: ThreadAspiration[]): { label: string; hue: Hue | undefined } | null {
  const a = aspirations.find((x) => x.aspirationId === message.aspirationId);
  return a ? { label: a.shortName, hue: asHue(a.hue) } : null;
}

/** A filter chip's text: a short name as it is, a long title cut short. */
export function chipLabel(name: string): string {
  return name.length <= CHIP_MAX ? name : `${name.slice(0, CHIP_MAX).trimEnd()}…`;
}

/** What waits on the user: a proposal to Review, per Aspiration. */
export function pinnedProposals(aspirations: ThreadAspiration[], filter: string | null = null) {
  return aspirations
    .filter((a) => a.pendingVersionId !== null && (filter === null || a.aspirationId === filter))
    .map((a) => ({ aspirationId: a.aspirationId, shortName: a.shortName, hue: a.hue, versionId: a.pendingVersionId! }));
}

/** Chips under the Coach's last message, while it is still the last word. */
export function quickReplies(messages: ChatMessage[]): string[] {
  const last = messages.at(-1);
  const replies = last?.role === 'coach' ? last.cardData?.quickReplies : undefined;
  return Array.isArray(replies) ? replies.filter((r): r is string => typeof r === 'string' && r.length > 0) : [];
}

/** The Coach tab's badge: Coach messages since the thread was last read, plus proposals waiting. */
export function tabBadge(messages: ChatMessage[], lastReadAt: string | null, aspirations: ThreadAspiration[]): number {
  const unread = messages.filter((m) => m.role === 'coach' && (lastReadAt === null || m.createdAt > lastReadAt)).length;
  return unread + pinnedProposals(aspirations).length;
}
