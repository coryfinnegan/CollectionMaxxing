import type { ReactNode } from 'react';
import { View } from 'react-native';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { useDialogBehavior } from '@/lib/a11y/useDialogBehavior';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { maybeEntering, motionMs } from './motion';
import { Button } from './Button';
import { Text } from './Text';

type SheetProps = { title: string; onClose: () => void; children?: ReactNode; testID?: string };

/** A panel that opens inline above the content, with a title and a Close button. */
export function Sheet({ title, onClose, children, testID }: SheetProps) {
  const dialog = useDialogBehavior(onClose);
  const reduceMotion = useReduceMotion();
  return (
    <Animated.View
      {...dialog}
      testID={testID}
      entering={maybeEntering(reduceMotion, FadeInDown.duration(motionMs.base))}
      style={styles.panel}
    >
      <View style={styles.header}>
        <Text variant="heading" accessibilityRole="header" style={styles.title}>
          {title}
        </Text>
        <Button label="Close" accessibilityLabel={`Close ${title}`} variant="secondary" onPress={onClose} />
      </View>
      {children}
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  panel: {
    gap: theme.spacing.md,
    padding: theme.spacing.lg,
    borderRadius: theme.radius.lg,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
  header: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  title: { flex: 1 },
}));
