import { ActivityIndicator, Pressable, type PressableProps } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type ButtonProps = Omit<PressableProps, 'children'> & {
  label: string;
  variant?: 'primary' | 'secondary' | 'danger';
  busy?: boolean;
};

export function Button({ label, variant = 'primary', busy = false, disabled, style, ...rest }: ButtonProps) {
  const colors = useColors();
  const isDisabled = disabled || busy;
  const background = variant === 'primary' ? colors.scan : 'transparent';
  const borderColor = variant === 'danger' ? colors.danger : variant === 'secondary' ? colors.control : colors.scan;
  const textColor = variant === 'primary' ? colors.onScan : variant === 'danger' ? colors.danger : colors.ink;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: isDisabled, busy }}
      disabled={isDisabled}
      {...rest}
      style={(state) => [
        {
          minHeight: 44,
          paddingHorizontal: spacing.lg,
          borderRadius: radius.md,
          borderWidth: 1,
          borderColor,
          backgroundColor: background,
          alignItems: 'center',
          justifyContent: 'center',
          flexDirection: 'row',
          gap: spacing.sm,
          opacity: isDisabled ? 0.5 : state.pressed ? 0.8 : 1,
        },
        typeof style === 'function' ? style(state) : style,
      ]}
    >
      {busy ? <ActivityIndicator color={textColor} /> : null}
      <Text variant="label" style={{ color: textColor }}>
        {label}
      </Text>
    </Pressable>
  );
}
