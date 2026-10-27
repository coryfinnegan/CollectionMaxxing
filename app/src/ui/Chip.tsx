// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Platform, Pressable, useWindowDimensions } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { WIDE_BREAKPOINT } from '@/theme/tokens';
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
  const press = usePressFeedback('select');
  const reduceMotion = useReduceMotion();
  // A finger needs 44 pt: on a phone-width web screen the chip is that tall itself (react-native-web ignores hitSlop); a pointer gets the compact 36.
  const { width } = useWindowDimensions();
  const minHeight = Platform.OS === 'web' && width < WIDE_BREAKPOINT ? 44 : 36;
  return (
    // The layout animation sits on a wrapper: the press feedback owns this view's opacity, and Reanimated warns when both share one view.
    <Animated.View layout={maybeLayout(reduceMotion)}>
      <Animated.View style={press.style} entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick))}>
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
          style={styles.chip({ minHeight, selected })}
        >
          <Text variant="label" style={styles.label(selected)}>
            {label}
          </Text>
        </Pressable>
      </Animated.View>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  chip: ({ minHeight, selected }: { minHeight: number; selected: boolean }) => ({
    minHeight,
    minWidth: minHeight === 44 ? 44 : undefined,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.pill,
    borderWidth: 1,
    borderColor: selected ? theme.colors.accent : theme.colors.control,
    backgroundColor: selected ? theme.colors.accent : theme.colors.surface,
  }),
  label: (selected: boolean) => ({ color: selected ? theme.colors.onAccent : theme.colors.ink }),
}));
