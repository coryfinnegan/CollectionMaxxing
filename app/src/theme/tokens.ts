// Design tokens from docs/06 "Visual direction". Dark values are derived: same hues, inverted ground.
import { useColorScheme } from 'react-native';

export const lightColors = {
  ground: '#F3F4F6',
  surface: '#FFFFFF',
  ink: '#1A1C22',
  muted: '#5E6472',
  line: '#DADDE3',
  scan: '#2348D8',
  onScan: '#FFFFFF',
  sticker: '#F26B1D',
  stickerText: '#B9480A',
  laser: '#FF2D3A',
  receipt: '#FBFBF7',
  danger: '#B3261E',
};

export type Colors = typeof lightColors;

export const darkColors: Colors = {
  ground: '#101218',
  surface: '#1B1E26',
  ink: '#F1F2F5',
  muted: '#A3A9B7',
  line: '#2E323D',
  scan: '#7C97FF',
  onScan: '#0B1030',
  sticker: '#F26B1D',
  stickerText: '#FF9A5C',
  laser: '#FF2D3A',
  receipt: '#FBFBF7',
  danger: '#FF8A80',
};

export const spacing = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 } as const;
export const radius = { sm: 6, md: 10, lg: 16, pill: 999 } as const;

// Archivo is not bundled yet; titles use the heaviest system weight as a stand-in for the condensed cut.
export const typeScale = {
  title: { fontSize: 28, lineHeight: 32, fontWeight: '800' },
  heading: { fontSize: 20, lineHeight: 26, fontWeight: '700' },
  body: { fontSize: 16, lineHeight: 22, fontWeight: '400' },
  label: { fontSize: 14, lineHeight: 18, fontWeight: '600' },
  caption: { fontSize: 13, lineHeight: 17, fontWeight: '400' },
} as const;

export type TextVariant = keyof typeof typeScale;

export const WIDE_BREAKPOINT = 768;

export function useColors(): Colors {
  return useColorScheme() === 'dark' ? darkColors : lightColors;
}
