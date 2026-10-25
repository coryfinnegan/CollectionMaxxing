// The web twin of the Lottie spot (#304): lottie-react-native's web player needs a
// peer dependency this app does not ship, so the web draws the same moments as
// still illustrations. Same props, same testID, still nothing under Reduce Motion.
import { View } from 'react-native';
import Svg, { Circle, Line, Path, Rect } from 'react-native-svg';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { useColors } from '@/theme/tokens';
import type { LottieSpotName } from './LottieSpot';

type LottieSpotProps = {
  name: LottieSpotName;
  size?: number;
  loop?: boolean;
  testID?: string;
};

export function LottieSpot({ name, size = 96, testID = 'lottie-spot' }: LottieSpotProps) {
  const colors = useColors();
  const reduceMotion = useReduceMotion();
  if (reduceMotion) return null;
  return (
    <View testID={testID} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
      <Svg width={size} height={size} viewBox="0 0 120 120">
        {name === 'success' ? (
          <>
            <Circle cx={60} cy={60} r={48} fill="none" stroke={colors.accent} strokeWidth={8} />
            <Path d="M36 64 L54 84 L86 42" fill="none" stroke={colors.accent} strokeWidth={10} strokeLinecap="round" strokeLinejoin="round" />
          </>
        ) : name === 'empty-library' ? (
          <>
            <Rect x={19} y={40} width={26} height={52} rx={6} fill="none" stroke={colors.muted} strokeWidth={6} />
            <Rect x={47} y={24} width={26} height={68} rx={6} fill="none" stroke={colors.accent} strokeWidth={6} />
            <Rect x={75} y={34} width={26} height={58} rx={6} fill="none" stroke={colors.muted} strokeWidth={6} />
            <Rect x={10} y={95} width={100} height={6} rx={3} fill={colors.muted} />
          </>
        ) : (
          <>
            <Circle cx={46} cy={46} r={27} fill="none" stroke={colors.muted} strokeWidth={8} />
            <Line x1={68} y1={68} x2={86} y2={86} stroke={colors.muted} strokeWidth={9} strokeLinecap="round" />
            <Circle cx={84} cy={30} r={6} fill={colors.accent} />
          </>
        )}
      </Svg>
    </View>
  );
}
