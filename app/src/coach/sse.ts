export type SseEvent = { name: string; data: any };

/** Incremental parser for Waypoint's SSE contract: `event:` and a one-line JSON `data:`, blank-line terminated. */
export function createSseParser() {
  let buffer = '';
  return {
    feed(chunk: string): SseEvent[] {
      buffer += chunk.replace(/\r\n/g, '\n');
      const events: SseEvent[] = [];
      let end: number;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const name = block.split('\n').find((l) => l.startsWith('event: '))?.slice(7);
        const data = block.split('\n').find((l) => l.startsWith('data: '))?.slice(6);
        if (name && data) events.push({ name, data: JSON.parse(data) });
      }
      return events;
    },
  };
}
