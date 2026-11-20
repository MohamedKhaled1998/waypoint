import { createActionSheet, partlyChips, SKIP_CHIPS, STOPPED_SHORT_CHIPS } from './sheet';

const tapped = '2026-01-07T07:30:00.000Z';

describe('Action sheet', () => {
  it('step 1 picks the result and step 2 is one optional follow-up', () => {
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    expect(sheet.state().step).toBe(1);
    sheet.choose('done');
    expect(sheet.state()).toMatchObject({ step: 2, choice: 'done', followUp: 'feeling' });
    expect(sheet.request('c1', tapped)).toEqual({
      scheduledActionId: 'a', clientId: 'c1', state: 'done', minutes: null, reasons: [], note: null, tappedAt: tapped,
    });
  });

  it('Done carries how it felt', () => {
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    sheet.choose('done');
    sheet.toggleReason('great');
    expect(sheet.request('c1', tapped)?.reasons).toEqual(['great']);
  });

  it('Partly needs minutes: 5-minute chips below the planned time, or Other', () => {
    expect(partlyChips(30)).toEqual([5, 10, 15, 20, 25]);
    expect(partlyChips(5)).toEqual([5]);
    // Never more than five: spread across the planned time.
    expect(partlyChips(75)).toEqual([15, 30, 40, 45, 60]);
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    sheet.choose('partly');
    expect(sheet.state().followUp).toBe('stoppedShort');
    expect(sheet.request('c1', tapped)).toBeNull();
    sheet.pickMinutes(20);
    sheet.toggleReason('tired');
    expect(sheet.request('c1', tapped)).toMatchObject({ state: 'partly', minutes: 20, reasons: ['tired'] });
    sheet.setOtherMinutes('12');
    expect(sheet.request('c1', tapped)?.minutes).toBe(12);
    sheet.setOtherMinutes('abc');
    expect(sheet.request('c1', tapped)).toBeNull();
  });

  it('Skip offers reason chips and free text under Something else', () => {
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    sheet.choose('skipped');
    expect(sheet.state().followUp).toBe('skipReason');
    sheet.toggleReason('no_time');
    sheet.toggleReason('something_else');
    expect(sheet.state().showFreeText).toBe(true);
    sheet.setNote('  family visit ');
    expect(sheet.request('c1', tapped)).toMatchObject({ state: 'skipped', reasons: ['no_time', 'something_else'], note: 'family visit' });
  });

  it('says the Coach will ask and ease the next days after hurt or unwell', () => {
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    sheet.choose('skipped');
    expect(sheet.state().coachWillAsk).toBe(false);
    sheet.toggleReason('unwell');
    expect(sheet.state().coachWillAsk).toBe(true);
    sheet.toggleReason('unwell');
    sheet.toggleReason('hurt');
    expect(sheet.state().coachWillAsk).toBe(true);
  });

  it('Not today is a Pause: this Action, everything for the rest of today, or a few days', () => {
    const sheet = createActionSheet({ scheduledActionId: 'a', minutes: 30 });
    expect(sheet.notTodayScopes).toEqual(['thisAction', 'everythingToday', 'fewDays']);
  });
});

describe('follow-up chips', () => {
  it('Partly and Skip both end in Something else, and Skip offers Forgot', () => {
    expect(STOPPED_SHORT_CHIPS.at(-1)).toBe('something_else');
    expect(SKIP_CHIPS).toContain('forgot');
    expect(SKIP_CHIPS.at(-1)).toBe('something_else');
  });
});
