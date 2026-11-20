import { settleForLine } from './settleFor';

const t = ((key: string, v: Record<string, string>) => (key === 'settleFor.whatBy' ? `${v.what} by ${v.date}` : key)) as never;

test('what and when, written once', () => {
  expect(settleForLine(t, '86 kg', '15 Jul 2027')).toBe('86 kg by 15 Jul 2027');
});

test('a picture that carries its own date gives way to the date shown', () => {
  expect(settleForLine(t, '86 kg instead of 83 kg by mid July', '15 Jul 2027')).toBe('86 kg instead of 83 kg by 15 Jul 2027');
});

test('only a later date', () => {
  expect(settleForLine(t, '', '15 Jul 2027')).toBe('15 Jul 2027');
});

test('only a smaller picture keeps its words', () => {
  expect(settleForLine(t, '86 kg by mid July', null)).toBe('86 kg by mid July');
});
