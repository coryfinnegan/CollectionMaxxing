// Unistyles 3 configuration (#305). The themes ARE the TS theme objects derived
// from tokens.json, so the token file stays the single source of truth; the wide
// breakpoint matches WIDE_BREAKPOINT (768). adaptiveThemes keeps the app
// following the system scheme, as useColorScheme does today.
import { StyleSheet } from 'react-native-unistyles';
import { castle, castleDark, darkColors, fixed, lightColors, mascot, radius, spacing, typeScale } from './tokens';

const shared = { spacing, radius, type: typeScale, fixed, mascot };
// The castle frame is day stone in the light theme and the torchlit hall in the dark theme (#363).
const appThemes = {
  light: { colors: lightColors, castle, ...shared },
  dark: { colors: darkColors, castle: castleDark, ...shared },
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

StyleSheet.configure({ themes: appThemes, breakpoints, settings: { adaptiveThemes: true } });
