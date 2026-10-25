import { Platform, Pressable, useWindowDimensions } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { radius, spacing, useColors, WIDE_BREAKPOINT } from '@/theme/tokens';
import { maybeEntering, maybeLayout, motionMs } from './motion';
import { Text } from './Text';
import { usePressFeedback } from './usePressFeedback';

type ChipProps = {
  label: string;
  selected?: boolean;
  onPress?: () => void;
  accessibilityLabel?: string;
  testID?: string;
};

/** A pill for filters and single-choice fields. Selected is shown by fill and by the accessibility state. */
export function Chip({ label, selected = false, onPress, accessibilityLabel, testID }: ChipProps) {
  const colors = useColors();
  const press = usePressFeedback('select');
  const reduceMotion = useReduceMotion();
  // A finger needs 44 pt: on a phone-width web screen the chip is that tall itself (react-native-web ignores hitSlop); a pointer gets the compact 36.
  const { width } = useWindowDimensions();
  const minHeight = Platform.OS === 'web' && width < WIDE_BREAKPOINT ? 44 : 36;
  return (
    <Animated.View
      style={press.style}
      entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick))}
      layout={maybeLayout(reduceMotion)}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={accessibilityLabel ?? label}
        accessibilityState={{ selected }}
        onPress={
          onPress
            ? () => {
                press.buzz();
                onPress();
              }
            : undefined
        }
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        testID={testID}
        hitSlop={{ top: 4, bottom: 4 }} // native: 36 pt to look, 44 pt to touch
        style={{
          minHeight,
          minWidth: minHeight === 44 ? 44 : undefined,
          alignItems: 'center',
          justifyContent: 'center',
          paddingHorizontal: spacing.md,
          borderRadius: radius.pill,
          borderWidth: 1,
          borderColor: selected ? colors.accent : colors.control,
          backgroundColor: selected ? colors.accent : colors.surface,
        }}
      >
        <Text variant="label" style={{ color: selected ? colors.onAccent : colors.ink }}>
          {label}
        </Text>
      </Pressable>
    </Animated.View>
  );
}
