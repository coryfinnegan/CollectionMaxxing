// Storybook-only stand-in for react-native-reanimated. ESM twin of the Jest
// mock in app/__mocks__/reanimated-impl.js (keep the two in sync): shared
// values are plain refs, animated styles evaluate once, animations resolve to
// their target. Stories show static primitives, so nothing of value is lost —
// and the real package cannot run under Vite (its Babel plugin and worklet
// runtime are Metro-only).
import { useRef } from 'react';
import { Easing as EasingRN, Image as ImageRN, Text as TextRN, View as ViewRN } from 'react-native';

export const useSharedValue = (initial) => useRef({ value: initial }).current;
export const useAnimatedStyle = (fn) => fn();
export const useDerivedValue = (fn) => ({ value: typeof fn === 'function' ? fn() : fn });
export const useAnimatedReaction = () => undefined;
export const withTiming = (value) => value;
export const withSpring = (value) => value;
export const withDelay = (_delay, value) => value;
export const withSequence = (...values) => values[values.length - 1];
export const withRepeat = (value) => value;
export const withDecay = (value) => value;
export const cancelAnimation = () => undefined;
export const runOnJS = (fn) => fn;
export const runOnUI = (fn) => fn;
export const runOnUIAsync = runOnUI;

const Animated = { View: ViewRN, Text: TextRN, Image: ImageRN };

export { Animated, EasingRN as Easing };
export default Animated;
