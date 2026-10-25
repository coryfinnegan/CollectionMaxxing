// A small Lottie moment (#304): the scan-success check and the empty-state spots.
// Still under Reduce Motion (renders nothing; the words next to it carry it), and
// decorative to screen readers (the title next to it says what matters).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import LottieView from 'lottie-react-native';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import emptyLibrary from '@/assets/lottie/empty-library.json';
import noResults from '@/assets/lottie/no-results.json';
import successCheck from '@/assets/lottie/success-check.json';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';

export type LottieSpotName = 'success' | 'empty-library' | 'no-results';

const SOURCES = { success: successCheck, 'empty-library': emptyLibrary, 'no-results': noResults } as const;

type LottieSpotProps = {
  name: LottieSpotName;
  size?: number;
  /** Empty states loop gently; the success check plays once. */
  loop?: boolean;
  testID?: string;
};

export function LottieSpot({ name, size = 96, loop = false, testID = 'lottie-spot' }: LottieSpotProps) {
  const reduceMotion = useReduceMotion();
  if (reduceMotion) return null;
  return (
    <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
      <LottieView source={SOURCES[name]} autoPlay loop={loop} style={styles.art(size)} testID={testID} />
    </View>
  );
}

const styles = StyleSheet.create(() => ({
  art: (size: number) => ({ width: size, height: size }),
}));
