// A quiet action written as words (Fix it, Undo, Check them): 44 pt to press, the shared press feedback, no frame.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Pressable } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { Text, usePressFeedback } from '@/ui';

export function TextAction({ label, accessibilityLabel, onPress, disabled }: { label: string; accessibilityLabel?: string; onPress(): void; disabled?: boolean }) {
  const press = usePressFeedback('tap');
  return (
    <Animated.View style={[styles.wrap, press.style]}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={accessibilityLabel ?? label}
        accessibilityState={disabled ? { disabled: true } : undefined}
        disabled={disabled}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        onPress={() => {
          press.buzz();
          onPress();
        }}
        style={styles.target}
      >
        <Text variant="label" tone={disabled ? 'muted' : 'accent'}>
          {label}
        </Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  wrap: { alignSelf: 'flex-start' },
  target: { minHeight: 44, minWidth: 44, paddingHorizontal: theme.spacing.sm, alignItems: 'center', justifyContent: 'center' },
}));
