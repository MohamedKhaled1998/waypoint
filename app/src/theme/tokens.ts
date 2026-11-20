// Look E "Fresh": neutral background, indigo-violet accent, one hue per Aspiration. Values from the prototype
// (`prototype/app`, fresh_light / fresh_dark).
const hueSolids = {
  coral: { light: '#F0643C', dark: '#FF7A59' },
  teal: { light: '#11A99A', dark: '#33CDBE' },
  amber: { light: '#D9960B', dark: '#F7BE4A' },
} as const;

/** A hue's solid colour, its card tint (the solid at low alpha) and its card border. */
const hue = (solid: string, dark: boolean) => ({ solid, tint: `${solid}${dark ? '1A' : '12'}`, border: `${solid}40` });

const shared = {
  radii: { sm: 10, md: 14, lg: 20, pill: 999 },
  space: { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 },
  font: { regular: 'System', bold: 'System' },
  // display 29 · title 19 · body 15 · small 13 · label 12; headings at ×1.25 line height, text at ×1.45.
  type: {
    title: { fontSize: 29, lineHeight: 36, fontWeight: '700' },
    heading: { fontSize: 19, lineHeight: 24, fontWeight: '700' },
    body: { fontSize: 15, lineHeight: 22, fontWeight: '400' },
    label: { fontSize: 13, lineHeight: 19, fontWeight: '600' },
    caption: { fontSize: 12, lineHeight: 17, fontWeight: '600' },
  },
} as const;

export const lightTheme = {
  ...shared,
  isDark: false,
  colors: {
    background: '#F7F7FB',
    surface: '#FFFFFF',
    surfaceAlt: '#EFEFF6',
    border: '#E3E4EE',
    text: '#121319',
    textMuted: '#5D6273',
    accent: '#6352E8',
    onAccent: '#FFFFFF',
    accentTint: '#ECEAFD',
    danger: '#E5533C',
    warn: '#E5533C',
    done: '#16A36A',
    partly: '#E0950F',
    skipped: '#8C91A1',
    scrim: 'rgba(12,10,30,0.6)',
    warningTint: '#FBF1DC',
    hues: { coral: hue(hueSolids.coral.light, false), teal: hue(hueSolids.teal.light, false), amber: hue(hueSolids.amber.light, false) },
  },
  shadow: {
    shadowColor: '#1B1A40',
    shadowOpacity: 0.06,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
    elevation: 1,
  },
} as const;

type Colors = {
  [K in keyof typeof lightTheme.colors]: K extends 'hues'
    ? { [H in keyof typeof hueSolids]: { solid: string; tint: string; border: string } }
    : string;
};

export const darkTheme = {
  ...shared,
  isDark: true,
  colors: {
    background: '#0B0B10',
    surface: '#15151D',
    surfaceAlt: '#1E1E29',
    border: '#272836',
    text: '#F3F3F7',
    textMuted: '#9A9DB0',
    accent: '#9184FF',
    onAccent: '#0B0B10',
    accentTint: '#221F45',
    danger: '#FF7A5C',
    warn: '#FF7A5C',
    done: '#4CD68D',
    partly: '#F7BE4A',
    skipped: '#6E7183',
    scrim: 'rgba(0,0,0,0.7)',
    warningTint: '#3A2F17',
    hues: { coral: hue(hueSolids.coral.dark, true), teal: hue(hueSolids.teal.dark, true), amber: hue(hueSolids.amber.dark, true) },
  } satisfies Colors,
  // Soft shadow on light cards only.
  shadow: { shadowColor: '#000000', shadowOpacity: 0, shadowRadius: 0, shadowOffset: { width: 0, height: 0 }, elevation: 0 },
} as const;

export type AppTheme = typeof lightTheme | typeof darkTheme;
export type Hue = keyof typeof hueSolids;
