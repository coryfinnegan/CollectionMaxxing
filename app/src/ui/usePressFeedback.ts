// Uniform press feedback (#302): every shared primitive shrinks a little and dims while
// pressed, and buzzes from the haptics vocabulary. The animation runs on Reanimated;
// in Jest the package mock renders it as a plain view.
import { useCallback } from 'react';
import { useAnimatedStyle, useSharedValue, withTiming } from 'react-native-reanimated';
import { haptics } from '@/lib/haptics';

export type PressHaptic = 'tap' | 'select' | 'commit' | 'none';

export function usePressFeedback(haptic: PressHaptic = 'tap') {
  const pressed = useSharedValue(0);
  // Explicit deps (only the stable shared value is read): the Babel plugin
  // infers this on native, but web bundlers without the plugin need it stated.
  const style = useAnimatedStyle(() => ({
    transform: [{ scale: 1 - pressed.value * 0.03 }],
    opacity: 1 - pressed.value * 0.15,
  }), []);
  const onPressIn = useCallback(() => {
    // Shared values are mutated by design; this is Reanimated's documented pattern.
    // eslint-disable-next-line react-hooks/immutability
    pressed.value = withTiming(1, { duration: 80 });
  }, [pressed]);
  const onPressOut = useCallback(() => {
    // eslint-disable-next-line react-hooks/immutability
    pressed.value = withTiming(0, { duration: 120 });
  }, [pressed]);
  const buzz = useCallback(() => {
    if (haptic === 'tap') void haptics.tap();
    else if (haptic === 'select') void haptics.select();
    else if (haptic === 'commit') void haptics.commit();
  }, [haptic]);
  return { style, onPressIn, onPressOut, buzz };
}
