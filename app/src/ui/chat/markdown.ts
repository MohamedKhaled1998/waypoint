/**
 * The small Markdown subset Coach bubbles render: **bold**, *italics* / _italics_, "- " or "* " bullets and "1. "
 * numbered lines. Anything else stays as written. The prompts ask for plain prose; this is for when the model slips.
 */
export type Span = { text: string; bold?: boolean; italic?: boolean };
export type Block =
  | { kind: 'paragraph'; spans: Span[] }
  | { kind: 'bullet'; spans: Span[] }
  | { kind: 'numbered'; n: string; spans: Span[] };

export function parseMarkdown(text: string): Block[] {
  const blocks: Block[] = [];
  let para: string[] = [];
  const flush = () => {
    if (para.length) blocks.push({ kind: 'paragraph', spans: parseInline(para.join('\n')) });
    para = [];
  };
  for (const line of text.split('\n')) {
    const bullet = /^\s*[-*•]\s+(.*)$/.exec(line);
    const numbered = /^\s*(\d+)[.)]\s+(.*)$/.exec(line);
    if (bullet) {
      flush();
      blocks.push({ kind: 'bullet', spans: parseInline(bullet[1]!) });
    } else if (numbered) {
      flush();
      blocks.push({ kind: 'numbered', n: numbered[1]!, spans: parseInline(numbered[2]!) });
    } else if (line.trim() === '') {
      flush();
    } else {
      para.push(line.replace(/^#{1,6}\s+/, ''));
    }
  }
  flush();
  return blocks;
}

export function parseInline(text: string): Span[] {
  const spans: Span[] = [];
  // **bold**, __bold__, *italic*, _italic_ (an underscore inside a word is left alone).
  const re = /\*\*(.+?)\*\*|__(.+?)__|\*(?!\s)(.+?)(?<!\s)\*|(?<![\p{L}\p{N}])_(?!\s)(.+?)(?<!\s)_(?![\p{L}\p{N}])/gu;
  let last = 0;
  for (const m of text.matchAll(re)) {
    if (m.index! > last) spans.push({ text: text.slice(last, m.index) });
    const bold = m[1] ?? m[2];
    if (bold !== undefined) spans.push({ text: bold, bold: true });
    else spans.push({ text: (m[3] ?? m[4])!, italic: true });
    last = m.index! + m[0].length;
  }
  if (last < text.length) spans.push({ text: text.slice(last) });
  return spans;
}
