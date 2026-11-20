import { nextReveal } from './reveal';

test('a steady stream shows three characters a tick', () => {
  expect(nextReveal(0, 10)).toBe(3);
});

test('a burst is shown within a second of ticks', () => {
  let shown = 0;
  for (let i = 0; i < 33; i++) shown = nextReveal(shown, 600);
  expect(shown).toBe(600);
});

test('never past what has arrived', () => {
  expect(nextReveal(9, 10)).toBe(10);
  expect(nextReveal(10, 10)).toBe(10);
});
