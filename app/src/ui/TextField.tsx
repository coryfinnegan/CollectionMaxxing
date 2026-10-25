// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { TextInput, type TextInputProps, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

type TextFieldProps = TextInputProps & {
  label: string;
  /** Friendly error copy, shown under the field. */
  error?: string | null;
};

export function TextField({ label, error, style, ...rest }: TextFieldProps) {
  const { theme } = useUnistyles();
  return (
    <View style={styles.field}>
      <Text variant="label">{label}</Text>
      <TextInput
        accessibilityLabel={label}
        placeholderTextColor={theme.colors.muted}
        {...rest}
        style={[styles.input(!!error), style]}
      />
      {error ? (
        <Text variant="caption" tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  field: { gap: theme.spacing.xs },
  input: (hasError: boolean) => ({
    minHeight: 44,
    paddingHorizontal: theme.spacing.md,
    borderWidth: 1,
    borderColor: hasError ? theme.colors.danger : theme.colors.control,
    borderRadius: theme.radius.md,
    backgroundColor: theme.colors.surface,
    color: theme.colors.ink,
    fontSize: 16,
    fontFamily: theme.type.body.fontFamily,
  }),
}));
