// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { ActivityIndicator, Pressable, type PressableProps } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';
import { usePressFeedback, type PressHaptic } from './usePressFeedback';

type ButtonVariant = 'primary' | 'secondary' | 'danger';

type ButtonProps = Omit<PressableProps, 'children'> & {
  label: string;
  variant?: ButtonVariant;
  busy?: boolean;
  /** Commits (save, add, delete) buzz with weight; the default is a light tap. */
  haptic?: PressHaptic;
};

export function Button({ label, variant = 'primary', busy = false, disabled, style, haptic = 'tap', onPress, onPressIn, onPressOut, ...rest }: ButtonProps) {
  const { theme } = useUnistyles();
  const press = usePressFeedback(haptic);
  const isDisabled = disabled || busy;
  const textColor = variant === 'primary' ? theme.colors.onAccent : variant === 'danger' ? theme.colors.danger : theme.colors.ink;
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
          styles.button({ variant, disabled: isDisabled, pressed: state.pressed }),
          typeof style === 'function' ? style(state) : style,
        ]}
      >
        {busy ? <ActivityIndicator color={textColor} /> : null}
        <Text variant="label" style={styles.label(textColor)}>
          {label}
        </Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  button: ({ variant, disabled, pressed }: { variant: ButtonVariant; disabled: boolean; pressed: boolean }) => ({
    minHeight: 44,
    paddingHorizontal: theme.spacing.lg,
    borderRadius: theme.radius.md,
    borderWidth: 1,
    borderColor: variant === 'danger' ? theme.colors.danger : variant === 'secondary' ? theme.colors.control : theme.colors.accent,
    backgroundColor: variant === 'primary' ? theme.colors.accent : 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
    flexDirection: 'row',
    gap: theme.spacing.sm,
    opacity: disabled ? 0.5 : pressed ? 0.8 : 1,
  }),
  label: (color: string) => ({ color }),
}));
