import type { ReactNode } from 'react';
import { View } from 'react-native';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { useDialogBehavior } from '@/lib/a11y/useDialogBehavior';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { radius, spacing, useColors } from '@/theme/tokens';
import { maybeEntering, motionMs } from './motion';
import { Button } from './Button';
import { Text } from './Text';

type SheetProps = { title: string; onClose: () => void; children?: ReactNode; testID?: string };

/** A panel that opens inline above the content, with a title and a Close button. */
export function Sheet({ title, onClose, children, testID }: SheetProps) {
  const colors = useColors();
  const dialog = useDialogBehavior(onClose);
  const reduceMotion = useReduceMotion();
  return (
    <Animated.View
      {...dialog}
      testID={testID}
      entering={maybeEntering(reduceMotion, FadeInDown.duration(motionMs.base))}
      style={{
        gap: spacing.md,
        padding: spacing.lg,
        borderRadius: radius.lg,
        borderWidth: 1,
        borderColor: colors.line,
        backgroundColor: colors.surface,
      }}
    >
      <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
        <Text variant="heading" accessibilityRole="header" style={{ flex: 1 }}>
          {title}
        </Text>
        <Button label="Close" accessibilityLabel={`Close ${title}`} variant="secondary" onPress={onClose} />
      </View>
      {children}
    </Animated.View>
  );
}
