import { createSseParser } from './sse';

describe('SSE parser', () => {
  it('yields events split across arbitrary chunk boundaries', () => {
    const parser = createSseParser();
    const raw = 'event: delta\ndata: {"text":"Hel"}\n\nevent: delta\ndata: {"text":"lo"}\n\nevent: done\ndata: {"messageId":"m1"}\n\n';
    const events = [];
    for (let i = 0; i < raw.length; i += 7) events.push(...parser.feed(raw.slice(i, i + 7)));
    expect(events).toEqual([
      { name: 'delta', data: { text: 'Hel' } },
      { name: 'delta', data: { text: 'lo' } },
      { name: 'done', data: { messageId: 'm1' } },
    ]);
  });
});
