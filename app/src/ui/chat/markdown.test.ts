import { parseInline, parseMarkdown } from './markdown';

test('bold and italics become spans', () => {
  expect(parseInline('Try **Back in shape** or *Lean by July*.')).toEqual([
    { text: 'Try ' },
    { text: 'Back in shape', bold: true },
    { text: ' or ' },
    { text: 'Lean by July', italic: true },
    { text: '.' },
  ]);
});

test('an underscore inside a word and a lone star stay as written', () => {
  expect(parseInline('snake_case_name and 3 * 4')).toEqual([{ text: 'snake_case_name and 3 * 4' }]);
});

test('bullets, numbered lines and paragraphs are blocks', () => {
  expect(parseMarkdown('Here is the picture.\n\n- Sunday\n- Tuesday\n1. Join a gym\nDone.')).toEqual([
    { kind: 'paragraph', spans: [{ text: 'Here is the picture.' }] },
    { kind: 'bullet', spans: [{ text: 'Sunday' }] },
    { kind: 'bullet', spans: [{ text: 'Tuesday' }] },
    { kind: 'numbered', n: '1', spans: [{ text: 'Join a gym' }] },
    { kind: 'paragraph', spans: [{ text: 'Done.' }] },
  ]);
});

test('a half-streamed bold stays plain until it closes', () => {
  expect(parseInline('I suggest **Back in')).toEqual([{ text: 'I suggest **Back in' }]);
});

test('heading marks are dropped', () => {
  expect(parseMarkdown('## Your week')).toEqual([{ kind: 'paragraph', spans: [{ text: 'Your week' }] }]);
});
