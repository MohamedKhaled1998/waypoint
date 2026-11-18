import { readdirSync, readFileSync, statSync } from 'fs';
import { join } from 'path';

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sources(path);
    return /\.tsx?$/.test(name) && !/\.test\./.test(name) ? [path] : [];
  });
}

const files = sources(join(__dirname));

describe('app conventions', () => {
  it('layouts use start/end, never left/right', () => {
    const offenders = files.filter((f) =>
      /\b(left|right|margin(Left|Right)|padding(Left|Right)|border(Left|Right)\w*)\s*:|textAlign:\s*'(left|right)'/.test(
        readFileSync(f, 'utf8'),
      ),
    );
    expect(offenders).toEqual([]);
  });

  it('screens put no text literals in JSX; every string goes through i18next', () => {
    const screens = files.filter((f) => f.includes(`${join('src', 'app')}`) || /src[\\/]app[\\/]/.test(f));
    const offenders = screens.filter((f) => />\s*[A-Za-z][^<>{}]*</.test(readFileSync(f, 'utf8')));
    expect(offenders).toEqual([]);
  });
});
