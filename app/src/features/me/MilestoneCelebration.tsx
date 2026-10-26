// The milestone celebration (#362): a one-shot check with the shield dragon when
// the shelves climb a rung. It dismisses itself, never traps input, and renders
// nothing under Reduce Motion (the shields tell the story) or when dragons are
// off, like every element of this issue.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect } from 'react';
import { Pressable } from 'react-native';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { LottieSpot, Text } from '@/ui';
import { useDragonAppearance } from '@/ui/mascot/appearances';
import { PlacedMascot } from '@/ui/mascot/PlacedMascot';
import { motionMs } from '@/ui/motion';

/** How long the milestone stays up before it goes away by itself. */
export const MILESTONE_CELEBRATION_MS = 2200;

export function MilestoneCelebration({ rung, onDone }: { rung: number; onDone: () => void }) {
  const reduceMotion = useReduceMotion();
  const appearance = useDragonAppearance((s) => s.appearance);
  useEffect(() => {
    const timer = setTimeout(onDone, MILESTONE_CELEBRATION_MS);
    return () => clearTimeout(timer);
  }, [onDone]);
  if (reduceMotion || appearance === 'off') return null;
  return (
    <Animated.View entering={FadeInDown.duration(motionMs.base)}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`A new milestone. ${rung} items shelved. Dismiss.`}
        onPress={onDone}
        testID="milestone-celebration"
        style={styles.celebration}
      >
        <LottieSpot name="success" size={120} testID="milestone-spot" />
        <PlacedMascot pose="milestone" placement="state" size={96} testID="milestone-dragon" />
        <Text variant="heading">A new milestone</Text>
        <Text tone="muted">{rung} items shelved.</Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  celebration: {
    alignItems: 'center',
    gap: theme.spacing.sm,
    padding: theme.spacing.lg,
    borderRadius: theme.radius.lg,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
}));
