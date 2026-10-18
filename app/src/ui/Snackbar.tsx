import { useEffect } from 'react';
import { View } from 'react-native';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button } from './Button';
import { Text } from './Text';

type SnackbarProps = { message: string; actionLabel?: string; onAction?: () => void };

/** A short message with one optional action, such as "Deleted The Matrix" with Undo. */
export function Snackbar({ message, actionLabel, onAction }: SnackbarProps) {
  const colors = useColors();
  useEffect(() => {
    announceForScreenReader(message);
  }, [message]);
  return (
    <View
      accessibilityLiveRegion="polite"
      style={{
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.md,
        padding: spacing.md,
        borderRadius: radius.md,
        backgroundColor: colors.ink,
      }}
    >
      <Text style={{ flex: 1, color: colors.ground }}>{message}</Text>
      {actionLabel && onAction ? <Button label={actionLabel} variant="secondary" onPress={onAction} /> : null}
    </View>
  );
}
