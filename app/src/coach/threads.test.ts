import type { ChatMessage } from './chat';
import { chipLabel, filterThread, pinnedProposals, quickReplies, tabBadge, tagOf, type ThreadAspiration } from './threads';

const aspirations: ThreadAspiration[] = [
  { aspirationId: 'a1', shortName: 'Half marathon', hue: 'coral', pendingVersionId: 'v1' },
  { aspirationId: 'a2', shortName: 'Spanish', hue: 'teal', pendingVersionId: null },
];

let n = 0;
const msg = (over: Partial<ChatMessage>): ChatMessage => ({
  id: `m${++n}`, role: 'coach', text: 'hi', createdAt: `2026-01-05T09:0${n % 10}:00Z`, status: 'sent', ...over,
});

describe('filtering the thread by Aspiration', () => {
  const thread = [msg({ aspirationId: 'a1' }), msg({ aspirationId: 'a2' }), msg({ role: 'user' }), msg({ aspirationId: 'a1' })];

  it('shows everything with no filter', () => {
    expect(filterThread(thread, null)).toHaveLength(4);
  });

  it('shows one Aspiration’s messages only', () => {
    expect(filterThread(thread, 'a1').map((m) => m.aspirationId)).toEqual(['a1', 'a1']);
  });
});

describe('the Aspiration tag', () => {
  it('names the Aspiration by short name and hue, and nothing for an untagged message', () => {
    expect(tagOf(msg({ aspirationId: 'a2' }), aspirations)).toEqual({ label: 'Spanish', hue: 'teal' });
    expect(tagOf(msg({}), aspirations)).toBeNull();
    expect(tagOf(msg({ aspirationId: 'gone' }), aspirations)).toBeNull();
  });
});

describe('filter chip labels', () => {
  it('keeps a short name and truncates a long title', () => {
    expect(chipLabel('Spanish')).toBe('Spanish');
    expect(chipLabel('Start my own company this year')).toBe('Start my own co…');
  });
});

describe('the pinned bar', () => {
  it('holds the proposals still waiting for Review, one per Aspiration', () => {
    expect(pinnedProposals(aspirations)).toEqual([{ aspirationId: 'a1', shortName: 'Half marathon', hue: 'coral', versionId: 'v1' }]);
  });

  it('is empty when nothing waits', () => {
    expect(pinnedProposals([{ ...aspirations[0]!, pendingVersionId: null }])).toEqual([]);
  });

  it('narrows to the filtered Aspiration', () => {
    expect(pinnedProposals(aspirations, 'a2')).toEqual([]);
    expect(pinnedProposals(aspirations, 'a1')).toHaveLength(1);
  });
});

describe('quick replies', () => {
  it('belong to the Coach’s last message when it asks something and the user has not answered', () => {
    const q = msg({ cardData: { quickReplies: ['Yes', 'Not now'] } });
    expect(quickReplies([msg({}), q])).toEqual(['Yes', 'Not now']);
  });

  it('go away once the user answers, and never come from an earlier question', () => {
    const q = msg({ cardData: { quickReplies: ['Yes'] } });
    expect(quickReplies([q, msg({ role: 'user' })])).toEqual([]);
    expect(quickReplies([q, msg({ text: 'Great' })])).toEqual([]);
  });

  it('ignore anything that is not a list of strings', () => {
    expect(quickReplies([msg({ cardData: { quickReplies: 'Yes' } })])).toEqual([]);
    expect(quickReplies([msg({ cardData: { quickReplies: ['Yes', 4] } })])).toEqual(['Yes']);
  });
});

describe('the Coach tab badge', () => {
  it('counts Coach messages after the last read plus waiting proposals', () => {
    const thread = [
      msg({ createdAt: '2026-01-05T08:00:00Z' }),
      msg({ createdAt: '2026-01-05T10:00:00Z' }),
      msg({ role: 'user', createdAt: '2026-01-05T10:05:00Z' }),
      msg({ createdAt: '2026-01-05T11:00:00Z' }),
    ];
    expect(tabBadge(thread, '2026-01-05T09:00:00Z', aspirations)).toBe(2 + 1);
  });

  it('is zero when everything is read and nothing waits', () => {
    expect(tabBadge([msg({ createdAt: '2026-01-05T08:00:00Z' })], '2026-01-05T09:00:00Z', [])).toBe(0);
  });

  it('counts everything as unread when the thread was never opened', () => {
    expect(tabBadge([msg({}), msg({})], null, [])).toBe(2);
  });
});
