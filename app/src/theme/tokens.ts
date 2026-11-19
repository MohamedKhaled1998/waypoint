// Look E "Fresh": neutral background, indigo-violet accent, one hue per Aspiration.
const hues = {
  coral: { light: { solid: '#E8614F', tint: '#FDECE9' }, dark: { solid: '#FF8A7A', tint: '#3A2623' } },
  teal: { light: { solid: '#14917F', tint: '#E3F4F1' }, dark: { solid: '#4FD1BC', tint: '#1B3430' } },
  amber: { light: { solid: '#C98100', tint: '#FBF1DC' }, dark: { solid: '#F2B84B', tint: '#3A2F17' } },
} as const;

const shared = {
  radii: { sm: 10, md: 14, lg: 20, pill: 999 },
  space: { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 },
  font: { regular: 'System', bold: 'System' },
  type: {
    title: { fontSize: 28, lineHeight: 34, fontWeight: '800' },
    heading: { fontSize: 20, lineHeight: 26, fontWeight: '700' },
    body: { fontSize: 16, lineHeight: 22, fontWeight: '400' },
    label: { fontSize: 14, lineHeight: 18, fontWeight: '600' },
    caption: { fontSize: 12, lineHeight: 16, fontWeight: '500' },
  },
} as const;

export const lightTheme = {
  ...shared,
  isDark: false,
  colors: {
    background: '#F6F6F9',
    surface: '#FFFFFF',
    border: '#E2E2EA',
    text: '#16161D',
    textMuted: '#6B6B7B',
    accent: '#6352E8',
    onAccent: '#FFFFFF',
    accentTint: '#ECE9FD',
    danger: '#C93C3C',
    scrim: 'rgba(0,0,0,0.3)',
    warningTint: '#FBF1DC',
    hues: { coral: hues.coral.light, teal: hues.teal.light, amber: hues.amber.light },
  },
  shadow: {
    shadowColor: '#16161D',
    shadowOpacity: 0.06,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
    elevation: 2,
  },
} as const;

type Colors = {
  [K in keyof typeof lightTheme.colors]: K extends 'hues'
    ? { [H in keyof typeof hues]: { solid: string; tint: string } }
    : string;
};

export const darkTheme = {
  ...shared,
  isDark: true,
  colors: {
    background: '#121214',
    surface: '#1C1C20',
    border: '#32323A',
    text: '#F2F2F5',
    textMuted: '#A0A0AE',
    accent: '#9184FF',
    onAccent: '#121214',
    accentTint: '#2A2748',
    danger: '#FF7A7A',
    scrim: 'rgba(0,0,0,0.6)',
    warningTint: '#3A2F17',
    hues: { coral: hues.coral.dark, teal: hues.teal.dark, amber: hues.amber.dark },
  } satisfies Colors,
  // Soft shadow on light cards only.
  shadow: { shadowColor: '#000000', shadowOpacity: 0, shadowRadius: 0, shadowOffset: { width: 0, height: 0 }, elevation: 0 },
} as const;

export type AppTheme = typeof lightTheme | typeof darkTheme;
export type Hue = keyof typeof hues;
