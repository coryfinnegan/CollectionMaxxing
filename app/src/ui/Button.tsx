import { ActivityIndicator, Pressable, type PressableProps } from 'react-native';
import Animated from 'react-native-reanimated';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';
import { usePressFeedback, type PressHaptic } from './usePressFeedback';

type ButtonProps = Omit<PressableProps, 'children'> & {
  label: string;
  variant?: 'primary' | 'secondary' | 'danger';
  busy?: boolean;
  /** Commits (save, add, delete) buzz with weight; the default is a light tap. */
  haptic?: PressHaptic;
};

export function Button({ label, variant = 'primary', busy = false, disabled, style, haptic = 'tap', onPress, onPressIn, onPressOut, ...rest }: ButtonProps) {
  const colors = useColors();
  const press = usePressFeedback(haptic);
  const isDisabled = disabled || busy;
  const background = variant === 'primary' ? colors.accent : 'transparent';
  const borderColor = variant === 'danger' ? colors.danger : variant === 'secondary' ? colors.control : colors.accent;
  const textColor = variant === 'primary' ? colors.onAccent : variant === 'danger' ? colors.danger : colors.ink;
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={label}
        accessibilityState={{ disabled: isDisabled, busy }}
        disabled={isDisabled}
        {...rest}
        onPress={
          onPress
            ? (e) => {
                press.buzz();
                onPress(e);
              }
            : undefined
        }
        onPressIn={(e) => {
          press.onPressIn();
          onPressIn?.(e);
        }}
        onPressOut={(e) => {
          press.onPressOut();
          onPressOut?.(e);
        }}
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
    </Animated.View>
  );
}
