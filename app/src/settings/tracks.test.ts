import {
  QUIET_PRESETS,
  moveQuietHandle,
  moveSlotHandle,
  quietFromTrack,
  quietHours,
  quietToTrack,
  slotHours,
  slotWindow,
  splitSegments,
  stepBudget,
  timeLabel,
} from './tracks';

describe('Slot track (05:00–24:00, 15-minute snap)', () => {
  it('snaps a dragged handle to the nearest 15 minutes', () => {
    expect(moveSlotHandle({ start: 420, end: 465 }, 'start', 427).start).toBe(420);
    expect(moveSlotHandle({ start: 420, end: 465 }, 'start', 428).start).toBe(435);
    expect(moveSlotHandle({ start: 420, end: 465 }, 'end', 500).end).toBe(495);
  });

  it('keeps the handles inside 05:00–24:00 and at least one step apart', () => {
    expect(moveSlotHandle({ start: 420, end: 465 }, 'start', 100).start).toBe(300);
    expect(moveSlotHandle({ start: 420, end: 465 }, 'end', 5000).end).toBe(1440);
    expect(moveSlotHandle({ start: 420, end: 465 }, 'start', 900).start).toBe(450);
    expect(moveSlotHandle({ start: 420, end: 465 }, 'end', 0).end).toBe(435);
  });

  it('reads the server\'s 23:59 as 24:00 and writes 24:00 back', () => {
    expect(slotWindow('07:00', '23:59')).toEqual({ start: 420, end: 1440 });
    expect(timeLabel(1440)).toBe('24:00');
    expect(timeLabel(425 + 10)).toBe('07:15');
  });

  it('counts the hours a Slot holds over its days', () => {
    expect(slotHours(['mon', 'wed', 'fri'], { start: 420, end: 465 })).toBe(2.25);
  });
});

describe('quiet hours track (noon to noon)', () => {
  it('puts an overnight span in one piece', () => {
    // 22:00 is 10 h after noon; 07:00 is 19 h after noon.
    expect(quietToTrack('22:00', '07:00')).toEqual({ start: 600, end: 1140 });
    expect(quietFromTrack({ start: 600, end: 1140 })).toEqual({ start: '22:00', end: '07:00' });
    expect(quietHours({ start: 600, end: 1140 })).toBe(9);
  });

  it('snaps either handle to 15 minutes and never lets them meet', () => {
    expect(moveQuietHandle({ start: 600, end: 1140 }, 'start', 611).start).toBe(615);
    expect(moveQuietHandle({ start: 600, end: 1140 }, 'end', 1400).end).toBe(1395);
    expect(moveQuietHandle({ start: 600, end: 1140 }, 'start', 1300).start).toBe(1125);
    expect(moveQuietHandle({ start: 600, end: 1140 }, 'end', 20).end).toBe(615);
  });

  it('the presets are 22–07, 23–07 and 21–08', () => {
    expect(QUIET_PRESETS.map((p) => [p.start, p.end])).toEqual([
      ['22:00', '07:00'],
      ['23:00', '07:00'],
      ['21:00', '08:00'],
    ]);
    expect(QUIET_PRESETS.map((p) => quietHours(quietToTrack(p.start, p.end)!))).toEqual([9, 8, 11]);
  });

  it('the default 21:30–07:00 sits on the track', () => {
    expect(quietHours(quietToTrack('21:30', '07:00')!)).toBe(9.5);
  });

  it('a span that does not cross midnight has no place on the noon-to-noon track', () => {
    expect(quietToTrack('13:00', '15:00')).toEqual({ start: 60, end: 180 });
    expect(quietToTrack('08:00', '20:00')).toBeNull();
  });
});

describe('Time budget', () => {
  it('steps by half an hour between 1 and 40', () => {
    expect(stepBudget(5, 1)).toBe(5.5);
    expect(stepBudget(5, -1)).toBe(4.5);
    expect(stepBudget(1, -1)).toBe(1);
    expect(stepBudget(40, 1)).toBe(40);
  });

  it('splits the bar by Aspiration share in order, leaving the unshared rest empty', () => {
    const segments = splitSegments(
      [
        { aspirationId: 'a', shortName: 'Run', hue: 'coral', hours: 3 },
        { aspirationId: 'b', shortName: 'Spanish', hue: 'teal', hours: 1 },
      ],
      5,
    );
    expect(segments.map((s) => [s.hue, s.fraction])).toEqual([
      ['coral', 0.6],
      ['teal', 0.2],
    ]);
  });

  it('shows every Aspiration when the shares add up to more than the budget', () => {
    const segments = splitSegments(
      [
        { aspirationId: 'a', shortName: 'Run', hue: 'coral', hours: 4 },
        { aspirationId: 'b', shortName: 'Spanish', hue: 'teal', hours: 1 },
      ],
      4,
    );
    expect(segments.map((s) => [s.hue, s.fraction])).toEqual([
      ['coral', 0.8],
      ['teal', 0.2],
    ]);
  });
});
