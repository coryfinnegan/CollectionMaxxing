// The TS theme, derived from tokens.json (the single source of truth): Radix 12-step
// Sand neutrals with an Amber accent, dark-first. Nothing here invents a color.
import { useColorScheme, type TextStyle } from 'react-native';
import source from './tokens.json';

type ScaleName = keyof typeof source.scales;

function at(scale: string, step: number): string {
  return (source.scales[scale as ScaleName] as readonly string[])[step - 1];
}

export type Colors = {
  ground: string;
  surface: string;
  ink: string;
  muted: string;
  line: string;
  control: string;
  accent: string;
  onAccent: string;
  sticker: string;
  stickerText: string;
  laser: string;
  receipt: string;
  receiptInk: string;
  receiptMuted: string;
  danger: string;
};

function resolve(roles: Record<string, unknown>): Colors {
  return Object.fromEntries(
    Object.entries(roles).map(([key, ref]) => [key, Array.isArray(ref) ? at(ref[0] as string, ref[1] as number) : (ref as string)]),
  ) as Colors;
}

const schemeFixed = { laser: source.fixed.laser, receipt: source.fixed.receipt };

export const lightColors: Colors = { ...resolve(source.roles.light), ...schemeFixed };
export const darkColors: Colors = { ...resolve(source.roles.dark), ...schemeFixed };

/** Fixed values that never change with the scheme: pure black/white and the camera scrim. */
export const fixed = {
  black: source.fixed.black,
  white: source.fixed.white,
  scrim: source.fixed.scrim,
};

export const spacing = source.spacing;
export const radius = source.radius;

// Bricolage Grotesque for display and UI, Space Mono for catalog metadata. Loaded by
// fonts.ts before the first screen; the weights double as the fallback rendering.
type TypeEntry = TextStyle & { fontFamily: string };

type TypeScale = Record<string, TypeEntry> & {
  display: TypeEntry;
  title: TypeEntry;
  heading: TypeEntry;
  body: TypeEntry;
  label: TypeEntry;
  caption: TypeEntry;
  mono: TypeEntry;
};

export const typeScale = source.type as TypeScale;

export type TextVariant = keyof typeof typeScale;

export const WIDE_BREAKPOINT = 768;

export function useColors(): Colors {
  return useColorScheme() === 'dark' ? darkColors : lightColors;
}
