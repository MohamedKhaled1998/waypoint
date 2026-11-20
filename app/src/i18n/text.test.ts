import { lowerFirst, upperFirst } from './text';

test('a fragment on its own line starts with a capital', () => {
  expect(upperFirst('too early to tell — you’re on the Plan so far')).toBe('Too early to tell — you’re on the Plan so far');
});

test('the Coach’s words mid-sentence start lowercase', () => {
  expect(lowerFirst("You've joined a gym")).toBe("you've joined a gym");
});

test('"I" and acronyms keep their capitals', () => {
  expect(lowerFirst("I've run 5 km")).toBe("I've run 5 km");
  expect(lowerFirst('GP says yes')).toBe('GP says yes');
  expect(lowerFirst('5K in under 30')).toBe('5K in under 30');
});
