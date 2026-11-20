import { formatDay } from './dates';

const today = new Date(2026, 10, 20, 9);

test('a day this year has no year', () => {
  expect(formatDay('2026-11-22', { locale: 'en-GB', today })).toBe('Sun 22 Nov');
});

test('a day in another year carries it', () => {
  expect(formatDay('2027-07-15', { locale: 'en-GB', today })).toBe('Thu, 15 Jul 2027');
});

test('the weekday can be left out', () => {
  expect(formatDay('2027-07-15', { locale: 'en-GB', today, weekday: false })).toBe('15 Jul 2027');
});

test('anything that is not a day comes back as it was', () => {
  expect(formatDay('this_week', { today })).toBe('this_week');
});
