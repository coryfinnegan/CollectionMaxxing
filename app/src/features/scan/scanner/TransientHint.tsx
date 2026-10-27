// A scanner hint (#408): it shows the first time its condition holds, then fades after a moment or clears on a
// tap, and never shows again. The camera stays clear of permanent pills.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useRef, useState } from 'react';
import Animated, { FadeIn, FadeOut } from 'react-native-reanimated';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Chip } from '@/ui';
import { maybeEntering, motionMs } from '@/ui/motion';

export function TransientHint({
  visible,
  label,
  onPress,
  visibleMs = 4000,
}: {
  /** The condition: the hint shows the first time this holds. */
  visible: boolean;
  label: string;
  /** Runs on a tap, which also clears the hint. */
  onPress: () => void;
  /** How long the hint stays before it fades. Tests pass a short moment. */
  visibleMs?: number;
}) {
  const reduceMotion = useReduceMotion();
  const [shown, setShown] = useState(false);
  const [wasVisible, setWasVisible] = useState(visible);
  const done = useRef(false);
  if (wasVisible !== visible) {
    // The condition cleared (a snap landed): the hint goes with it. Adjusted during render, not in an effect.
    setWasVisible(visible);
    if (!visible) setShown(false);
  }
  useEffect(() => {
    if (!visible || done.current) return;
    done.current = true;
    setShown(true);
    const timer = setTimeout(() => setShown(false), visibleMs);
    return () => clearTimeout(timer);
  }, [visible, visibleMs]);
  if (!shown) return null;
  return (
    <Animated.View entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick))} exiting={reduceMotion ? undefined : FadeOut.duration(motionMs.settle)}>
      <Chip
        label={label}
        onPress={() => {
          setShown(false);
          onPress();
        }}
      />
    </Animated.View>
  );
}
