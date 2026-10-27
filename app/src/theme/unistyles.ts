// Unistyles 3 configuration (#305, #426). The themes ARE the TS theme objects
// derived from tokens.json, so the token file stays the single source of truth;
// the wide breakpoint matches WIDE_BREAKPOINT (768). Two themes, light and dark:
// an explicit Light/Dark choice pins the initial theme from the kept MMKV value
// for the first paint, while System follows the OS through adaptiveThemes.
import { StyleSheet } from 'react-native-unistyles';
import { readAppearanceChoice } from './appearance';
import { darkColors, fixed, lightColors, radius, spacing, typeScale } from './tokens';

const shared = { spacing, radius, type: typeScale, fixed };
const appThemes = {
  light: { colors: lightColors, ...shared },
  dark: { colors: darkColors, ...shared },
};
const breakpoints = { phone: 0, wide: 768 };

type AppThemes = typeof appThemes;
type AppBreakpoints = typeof breakpoints;

declare module 'react-native-unistyles' {
  // Empty on purpose: declaration merging registers the app themes/breakpoints.
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type
  export interface UnistylesThemes extends AppThemes {}
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type
  export interface UnistylesBreakpoints extends AppBreakpoints {}
}

const kept = readAppearanceChoice();
StyleSheet.configure({
  themes: appThemes,
  breakpoints,
  settings: kept === 'system' ? { adaptiveThemes: true } : { initialTheme: kept },
});
