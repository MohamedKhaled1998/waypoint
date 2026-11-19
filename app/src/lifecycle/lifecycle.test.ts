import { en } from '../i18n/en';
import { confirmFor, consistencyLine, menuItems } from './lifecycle';

function t(key: string, values: Record<string, string | number> = {}): string {
  const found = key.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown> | undefined)?.[k], en);
  if (typeof found !== 'string') throw new Error(`missing string ${key}`);
  return found.replace(/\{\{(\w+)\}\}/g, (_, n: string) => String(values[n]));
}

describe('the overflow menu', () => {
  it('offers Pause, Move to maintenance, Mark reached and Delete on an active Aspiration', () => {
    expect(menuItems('active', false)).toEqual(['pause', 'move_to_maintenance', 'mark_reached', 'delete']);
  });

  it('offers Resume instead of Pause on a paused one', () => {
    expect(menuItems('active', true)).toEqual(['resume', 'delete']);
  });

  it('offers a new chapter instead of Move to maintenance in maintenance', () => {
    expect(menuItems('maintenance', false)).toEqual(['pause', 'new_chapter', 'mark_reached', 'delete']);
  });

  it('offers only Delete once it is closed', () => {
    expect(menuItems('closed', false)).toEqual(['delete']);
  });
});

describe('the confirms', () => {
  it('asks before moving to maintenance, and says what it means', () => {
    expect(confirmFor('move_to_maintenance')).toMatchObject({ prefix: 'lifecycle.maintenance', destructive: false });
    expect(t('lifecycle.maintenance.body')).toMatch(/Coach/);
  });

  it('lets Mark reached keep it up in maintenance or close it keeping history', () => {
    expect(confirmFor('mark_reached')?.choices).toEqual(['keepUp', 'close']);
    expect(t('lifecycle.reached.close')).toMatch(/history/i);
  });

  it('makes Delete final and suggests Pause instead', () => {
    const c = confirmFor('delete');
    expect(c).toMatchObject({ destructive: true, choices: ['pauseInstead', 'confirm'] });
    expect(t('lifecycle.delete.body')).toMatch(/can.t be undone/i);
    expect(t('lifecycle.delete.pauseInstead')).toMatch(/pause/i);
  });

  it('does not suggest Pause instead when it is already paused', () => {
    expect(confirmFor('delete', true)?.choices).toEqual(['confirm']);
  });

  it('does not ask before Pause, Resume or a new chapter', () => {
    expect(confirmFor('pause')).toBeNull();
    expect(confirmFor('resume')).toBeNull();
    expect(confirmFor('new_chapter')).toBeNull();
  });
});

describe('consistency', () => {
  it('says weeks kept up of the last eight', () => {
    expect(consistencyLine({ weeksKeptUp: 5, ofWeeks: 8 }, t)).toBe('5 of the last 8 weeks kept up');
  });
});
