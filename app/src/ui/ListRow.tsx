// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { Pressable, View } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { maybeEntering, maybeLayout, motionMs } from './motion';
import { Text } from './Text';
import { usePressFeedback } from './usePressFeedback';

type ListRowProps = {
  title: string;
  subtitle?: string;
  /** Nesting level; each level indents the row. */
  depth?: number;
  trailing?: ReactNode;
  /** A decorative "›" for rows that open another screen. Hidden from assistive tech, so the row's name stays its visible words. */
  chevron?: boolean;
  onPress?: () => void;
  testID?: string;
};

export function ListRow({ title, subtitle, depth = 0, trailing, chevron = false, onPress, testID }: ListRowProps) {
  const press = usePressFeedback('tap');
  const reduceMotion = useReduceMotion();
  const entering = maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick));
  const layout = maybeLayout(reduceMotion);
  const body = (
    <View style={styles.row(depth)}>
      <View style={styles.text}>
        <Text>{title}</Text>
        {subtitle ? (
          <Text variant="caption" tone="muted">
            {subtitle}
          </Text>
        ) : null}
      </View>
      {trailing}
      {chevron ? (
        <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" aria-hidden>
          <Text tone="muted">›</Text>
        </View>
      ) : null}
    </View>
  );
  return onPress ? (
    // The layout animation sits on a wrapper: the press feedback owns this view's opacity, and Reanimated warns when both share one view.
    <Animated.View layout={layout}>
      <Animated.View style={press.style} entering={entering}>
        <Pressable
          accessibilityRole="button"
          // With a subtitle the name comes from the visible words themselves (no label to drift from them, WCAG 2.5.3); a lone title is its own label.
          accessibilityLabel={subtitle ? undefined : title}
          onPress={() => {
            press.buzz();
            onPress();
          }}
          onPressIn={press.onPressIn}
          onPressOut={press.onPressOut}
          testID={testID}
        >
          {body}
        </Pressable>
      </Animated.View>
    </Animated.View>
  ) : (
    <Animated.View entering={entering} layout={layout} testID={testID}>
      {body}
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: (depth: number) => ({
    flexDirection: 'row',
    alignItems: 'center',
    // Crowded trailing controls drop under the text instead of squeezing it into a sliver.
    flexWrap: 'wrap',
    columnGap: theme.spacing.md,
    rowGap: theme.spacing.sm,
    minHeight: 48,
    paddingVertical: theme.spacing.sm,
    paddingLeft: depth * theme.spacing.xl,
    borderBottomWidth: 1,
    borderBottomColor: theme.colors.line,
  }),
  text: { flex: 1, minWidth: 160 },
}));
