// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { Button } from './Button';
import { Text } from './Text';

type SnackbarProps = { message: string; actionLabel?: string; onAction?: () => void };

/** A short message with one optional action, such as "Deleted The Matrix" with Undo. */
export function Snackbar({ message, actionLabel, onAction }: SnackbarProps) {
  useEffect(() => {
    announceForScreenReader(message);
  }, [message]);
  return (
    <View accessibilityLiveRegion="polite" style={styles.bar}>
      <Text style={styles.message}>{message}</Text>
      {actionLabel && onAction ? <Button label={actionLabel} variant="secondary" onPress={onAction} /> : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.md,
    padding: theme.spacing.md,
    borderRadius: theme.radius.md,
    backgroundColor: theme.colors.ink,
  },
  message: { flex: 1, color: theme.colors.ground },
}));
