import { StyleSheet } from 'react-native-unistyles';
import { darkTheme, lightTheme, type AppTheme } from './tokens';

const themes = { light: lightTheme, dark: darkTheme };

StyleSheet.configure({
  themes,
  settings: { adaptiveThemes: true },
});

type AppThemes = typeof themes;

declare module 'react-native-unistyles' {
  export interface UnistylesThemes extends AppThemes {}
}

export type { AppTheme };
