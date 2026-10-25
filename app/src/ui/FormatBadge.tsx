import { View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

/** A small label for a release format ("DVD", "Paperback"). */
export function FormatBadge({ label }: { label: string }) {
  const colors = useColors();
  return (
    <View
      style={{
        alignSelf: 'flex-start',
        paddingHorizontal: spacing.sm,
        paddingVertical: 2,
        borderRadius: radius.sm,
        borderWidth: 1,
        borderColor: colors.line,
        backgroundColor: colors.surface,
      }}
    >
      <Text variant="mono" tone="muted">
        {label}
      </Text>
    </View>
  );
}
