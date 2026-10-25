import type { ReactNode } from 'react';
import { Pressable, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';
import { usePressFeedback } from './usePressFeedback';

type ListRowProps = {
  title: string;
  subtitle?: string;
  /** Nesting level; each level indents the row. */
  depth?: number;
  trailing?: ReactNode;
  onPress?: () => void;
  testID?: string;
};

export function ListRow({ title, subtitle, depth = 0, trailing, onPress, testID }: ListRowProps) {
  const colors = useColors();
  const press = usePressFeedback('tap');
  const body = (
    <View
      style={{
        flexDirection: 'row',
        alignItems: 'center',
        // Crowded trailing controls drop under the text instead of squeezing it into a sliver.
        flexWrap: 'wrap',
        columnGap: spacing.md,
        rowGap: spacing.sm,
        minHeight: 48,
        paddingVertical: spacing.sm,
        paddingLeft: depth * spacing.xl,
        borderBottomWidth: 1,
        borderBottomColor: colors.line,
      }}
    >
      <View style={{ flex: 1, minWidth: 160 }}>
        <Text>{title}</Text>
        {subtitle ? (
          <Text variant="caption" tone="muted">
            {subtitle}
          </Text>
        ) : null}
      </View>
      {trailing}
    </View>
  );
  return onPress ? (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={subtitle ? `${title}, ${subtitle}` : title}
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
  ) : (
    <View testID={testID}>{body}</View>
  );
}
