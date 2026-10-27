// The scan-success moment (#304): the hook already buzzed success when the commit
// landed; this is the visual half — a short Lottie check that dismisses itself and
// never blocks input. The toast below keeps the words and the Undo.
import { useEffect } from 'react';
import { Pressable } from 'react-native';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { radius, spacing, useColors } from '@/theme/tokens';
import { LottieSpot } from '@/ui';
import { motionMs } from '@/ui/motion';

/** How long the check stays up before it goes away by itself. */
export const CELEBRATION_MS = 2200;

/**
 * The check plays 1.5x: 72 frames at 60fps land in 800ms, under the one-second
 * budget (#362). The overlay itself never blocks input (tap to dismiss, and the
 * toast below carries the words), so the next scan and the post-batch summary
 * never wait for it.
 */
export const CELEBRATION_SPEED = 1.5;

export function CommitCelebration({ message, onDone }: { message: string; onDone: () => void }) {
  const colors = useColors();
  const reduceMotion = useReduceMotion();
  useEffect(() => {
    const timer = setTimeout(onDone, CELEBRATION_MS);
    return () => clearTimeout(timer);
  }, [onDone]);
  // Under Reduce Motion the toast below tells the story on its own.
  if (reduceMotion) return null;
  return (
    <Animated.View entering={FadeInDown.duration(motionMs.base)}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${message}. Dismiss.`}
        onPress={onDone}
        testID="commit-celebration"
        style={{
          alignItems: 'center',
          gap: spacing.sm,
          padding: spacing.lg,
          borderRadius: radius.lg,
          borderWidth: 1,
          borderColor: colors.line,
          backgroundColor: colors.surface,
        }}
      >
        <LottieSpot name="success" size={120} speed={CELEBRATION_SPEED} testID="celebration-spot" />
      </Pressable>
    </Animated.View>
  );
}
