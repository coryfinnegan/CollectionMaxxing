// The navigators' theme, built from our own tokens so the header, tab bar and screen backgrounds match the content in light and dark.
import { DarkTheme, DefaultTheme, type Theme } from 'expo-router';
import { useColorScheme } from 'react-native';
import { darkColors, lightColors, type Colors } from './tokens';

export function navigationThemeFor(dark: boolean, colors: Colors = dark ? darkColors : lightColors): Theme {
  const base = dark ? DarkTheme : DefaultTheme;
  return {
    ...base,
    dark,
    colors: {
      ...base.colors,
      primary: colors.scan,
      background: colors.ground,
      card: colors.surface,
      text: colors.ink,
      border: colors.line,
      notification: colors.sticker,
    },
  };
}

export function useNavigationTheme(): Theme {
  return navigationThemeFor(useColorScheme() === 'dark');
}
