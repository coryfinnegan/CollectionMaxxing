import type { ReactNode } from 'react';
import { Pressable, View } from 'react-native';
import { spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

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
  const body = (
    <View
      style={{
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.md,
        minHeight: 48,
        paddingVertical: spacing.sm,
        paddingLeft: depth * spacing.xl,
        borderBottomWidth: 1,
        borderBottomColor: colors.line,
      }}
    >
      <View style={{ flex: 1 }}>
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
    <Pressable accessibilityRole="button" accessibilityLabel={title} onPress={onPress} testID={testID}>
      {body}
    </Pressable>
  ) : (
    <View testID={testID}>{body}</View>
  );
}
