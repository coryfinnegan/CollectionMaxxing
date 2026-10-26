// The dragon mascot (#365): one pose of the shared layered vector. Decorative to
// screen readers unless the pose conveys state, and Reduce Motion always gets
// the static pose instead of the loop.
import { useEffect, type ReactNode } from 'react';
import { View } from 'react-native';
import Animated, { Easing, useAnimatedStyle, useSharedValue, withRepeat, withTiming } from 'react-native-reanimated';
import Svg from 'react-native-svg';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { useDragonAppearance } from './appearances';
import { DragonLayers } from './layers';
import { poses, type MascotPose, type PoseMotion } from './poses';

const loops: Record<PoseMotion, { distance: number; duration: number }> = {
  /** A celebrating hop: high and quick. */
  hop: { distance: -12, duration: 450 },
  /** A peeking bob: small and slow. */
  bob: { distance: -5, duration: 1600 },
  /** Fast reading: a barely-there shiver. */
  flutter: { distance: -2, duration: 220 },
};

function Loop({ kind, testID, children }: { kind: PoseMotion; testID: string; children: ReactNode }) {
  const { distance, duration } = loops[kind];
  const offset = useSharedValue(0);
  const style = useAnimatedStyle(() => ({ transform: [{ translateY: offset.value }] }), [offset]);
  useEffect(() => {
    // Shared values are mutated by design; this is Reanimated's documented pattern.
    // eslint-disable-next-line react-hooks/immutability
    offset.value = withRepeat(withTiming(distance, { duration, easing: Easing.inOut(Easing.ease) }), -1, true);
  }, [offset, distance, duration]);
  return (
    <Animated.View testID={testID} style={style}>
      {children}
    </Animated.View>
  );
}

type MascotProps = {
  pose: MascotPose;
  size?: number;
  /** When the pose conveys state, the label screen readers announce. Without it the mascot stays decorative. */
  accessibilityLabel?: string;
  testID?: string;
};

export function Mascot({ pose, size = 160, accessibilityLabel, testID = 'mascot' }: MascotProps) {
  const reduceMotion = useReduceMotion();
  const appearance = useDragonAppearance((s) => s.appearance);
  const config = poses[pose];
  // Loops play on Full only: Reduce Motion holds every pose still, and Minimal holds them still too.
  const loop = !reduceMotion && appearance === 'full' ? config.motion : undefined;
  const art = (
    <Svg width={size} height={size} viewBox="0 0 240 240">
      <DragonLayers config={config} />
    </Svg>
  );
  // Decorative by default: the surrounding content carries the meaning. A pose
  // that conveys state (loading, empty, error) names itself through the label.
  const a11y = accessibilityLabel ? { accessibilityRole: 'image' as const, accessibilityLabel } : { accessible: false as const };
  return (
    <View testID={testID} {...a11y}>
      {loop ? (
        <Loop kind={loop} testID={`${testID}-motion`}>
          {art}
        </Loop>
      ) : (
        art
      )}
    </View>
  );
}
