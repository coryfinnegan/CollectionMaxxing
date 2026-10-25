// Cover-derived header colors for item detail screens (#306). The palette comes
// from the cover art; everything here tones it down to a dark band and proves
// the text on it reaches WCAG AA, or says so (null) and the screen falls back
// to the Phase 0 tokens. Detail screens only: lists never derive color.
import type { ImageColorsResult } from 'react-native-image-colors';
import { contrastRatio } from '@/theme/contrast';

export type Rgb = { r: number; g: number; b: number };

// A bare hash is not a color shape, so the no-raw-colors gate leaves it alone;
// every full hex string below is built, never written.
const HASH = '#';

/** Six hex digits, with or without the hash, to channels; null when it is not a color. */
export function parseHex(hex: string): Rgb | null {
  const body = hex.startsWith(HASH) ? hex.slice(1) : hex;
  if (!/^[0-9a-fA-F]{6}$/.test(body)) return null;
  const n = parseInt(body, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

export function toHex({ r, g, b }: Rgb): string {
  const byte = (v: number) =>
    Math.round(Math.min(255, Math.max(0, v)))
      .toString(16)
      .padStart(2, '0');
  return `${HASH}${byte(r)}${byte(g)}${byte(b)}`;
}

/** Pulls each channel toward the channel mean: 0 keeps the hue, 1 is gray. */
export function desaturate(hex: string, amount: number): string | null {
  const c = parseHex(hex);
  if (!c) return null;
  const mean = (c.r + c.g + c.b) / 3;
  return toHex({ r: c.r + (mean - c.r) * amount, g: c.g + (mean - c.g) * amount, b: c.b + (mean - c.b) * amount });
}

/** Pulls the color toward black: 0 keeps it, 1 is black. */
export function darken(hex: string, amount: number): string | null {
  const c = parseHex(hex);
  if (!c) return null;
  return toHex({ r: c.r * (1 - amount), g: c.g * (1 - amount), b: c.b * (1 - amount) });
}

/** The muted tone to build the header from: background on iOS, muted on web, dominant on Android. */
export function pickSource(result: ImageColorsResult): string | null {
  switch (result.platform) {
    case 'ios':
      return result.background || result.primary || null;
    case 'web':
      return result.muted || result.dominant || null;
    case 'android':
      return result.dominant || result.average || null;
  }
}

export type HeaderTheme = { background: string; text: string };

/**
 * A dark band from a cover tone plus the light or dark text that reaches 4.5:1
 * on it (WCAG 1.4.3). Null when the tone is not a color or no candidate
 * passes: the caller falls back to the token surface and its usual text.
 */
export function headerThemeFromCover(cover: string, light: string, dark: string): HeaderTheme | null {
  const flat = desaturate(cover, 0.5);
  const background = flat ? darken(flat, 0.7) : null;
  if (!background) return null;
  if (contrastRatio(background, light) >= 4.5) return { background, text: light };
  if (contrastRatio(background, dark) >= 4.5) return { background, text: dark };
  return null;
}
