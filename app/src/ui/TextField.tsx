// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRef } from 'react';
import { TextInput, type TextInputProps, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useReportInputFocus } from './keyboardScroll';
import { Text } from './Text';

type TextFieldProps = TextInputProps & {
  label: string;
  /** Friendly error copy, shown under the field. */
  error?: string | null;
};

export function TextField({ label, error, style, onFocus, ...rest }: TextFieldProps) {
  const { theme } = useUnistyles();
  // Inside a keyboard-avoiding Screen (#442), focusing scrolls this field
  // above the keyboard. Outside one the scope is absent and focus is untouched.
  const inputRef = useRef<TextInput>(null);
  const reportFocus = useReportInputFocus();
  return (
    <View style={styles.field}>
      <Text variant="label">{label}</Text>
      <TextInput
        ref={inputRef}
        accessibilityLabel={label}
        placeholderTextColor={theme.colors.muted}
        {...rest}
        onFocus={(e) => {
          reportFocus?.(inputRef.current);
          onFocus?.(e);
        }}
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
