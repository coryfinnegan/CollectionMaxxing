// Jest stand-in for react-native-reanimated: shared values are plain refs, animated
// styles evaluate once, and animations resolve to their target. Press feedback is
// visual; behaviour tests assert what was pressed, not how it moved.
const React = require('react');
const { Easing: EasingRN, Image: ImageRN, Text: TextRN, View: ViewRN } = require('react-native');

const useSharedValue = (initial) => React.useRef({ value: initial }).current;
const useAnimatedStyle = (fn) => fn();
const useDerivedValue = (fn) => ({ value: typeof fn === 'function' ? fn() : fn });
const useAnimatedReaction = () => undefined;
const withTiming = (value) => value;
const withSpring = (value) => value;
const withDelay = (_delay, value) => value;
const withSequence = (...values) => values[values.length - 1];
const withRepeat = (value) => value;
const withDecay = (value) => value;
const cancelAnimation = () => undefined;
const runOnJS = (fn) => fn;
const runOnUI = (fn) => fn;

const Animated = { View: ViewRN, Text: TextRN, Image: ImageRN };

module.exports = {
  __esModule: true,
  default: Animated,
  Animated,
  Easing: EasingRN,
  useSharedValue,
  useAnimatedStyle,
  useDerivedValue,
  useAnimatedReaction,
  withTiming,
  withSpring,
  withDelay,
  withSequence,
  withRepeat,
  withDecay,
  cancelAnimation,
  runOnJS,
  runOnUI,
  runOnUIAsync: runOnUI,
};
