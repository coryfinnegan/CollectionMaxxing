import { TextInput, type TextInputProps, View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type TextFieldProps = TextInputProps & {
  label: string;
  /** Friendly error copy, shown under the field. */
  error?: string | null;
};

export function TextField({ label, error, style, ...rest }: TextFieldProps) {
  const colors = useColors();
  return (
    <View style={{ gap: spacing.xs }}>
      <Text variant="label">{label}</Text>
      <TextInput
        accessibilityLabel={label}
        placeholderTextColor={colors.muted}
        {...rest}
        style={[
          {
            minHeight: 44,
            paddingHorizontal: spacing.md,
            borderWidth: 1,
            borderColor: error ? colors.danger : colors.control,
            borderRadius: radius.md,
            backgroundColor: colors.surface,
            color: colors.ink,
            fontSize: 16,
          },
          style,
        ]}
      />
      {error ? (
        <Text variant="caption" tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
    </View>
  );
}
