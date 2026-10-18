import { Pressable } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

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
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={{ selected }}
      onPress={onPress}
      testID={testID}
      hitSlop={{ top: 4, bottom: 4 }} // 36 pt tall to look, 44 pt to touch
      style={{
        minHeight: 36,
        justifyContent: 'center',
        paddingHorizontal: spacing.md,
        borderRadius: radius.pill,
        borderWidth: 1,
        borderColor: selected ? colors.scan : colors.control,
        backgroundColor: selected ? colors.scan : colors.surface,
      }}
    >
      <Text variant="label" style={{ color: selected ? colors.onScan : colors.ink }}>
        {label}
      </Text>
    </Pressable>
  );
}
