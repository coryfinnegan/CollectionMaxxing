// The motion vocabulary (#304): one set of durations and the reduce-motion gates.
// Every entering, layout, and shared animation in the app goes through here, so the
// OS reduce-motion setting quiets them all in one place.
import { Platform } from 'react-native';
import { Layout } from 'react-native-reanimated';

/** Entering and layout animations stay subtle and fast. */
export const motionMs = { quick: 150, base: 200, settle: 250 } as const;

/** The animation, or nothing when Reduce Motion is on (the content appears at once). */
export function maybeEntering<T>(reduceMotion: boolean, animation: T): T | undefined {
  return reduceMotion ? undefined : animation;
}

/** The shared layout animation, or nothing when Reduce Motion is on. */
export function maybeLayout(reduceMotion: boolean) {
  return reduceMotion ? undefined : Layout.duration(motionMs.base);
}

/** Grid tiles settle in a ripple: 20ms apart, capped so a long list never ripples far. */
export function staggerDelay(index: number): number {
  return Math.min(Math.max(index, 0), 8) * 20;
}

/** The cover tag for the library-to-detail shared transition: unset on the web and under Reduce Motion, where the plain push is the transition. */
export function sharedCoverTag(reduceMotion: boolean, itemId: string): string | undefined {
  if (reduceMotion || Platform.OS === 'web') return undefined;
  return `cover-${itemId}`;
}
