// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

/** A small label for a release format ("DVD", "Paperback"). */
export function FormatBadge({ label }: { label: string }) {
  return (
    <View style={styles.badge}>
      <Text variant="mono" tone="muted">
        {label}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  badge: {
    alignSelf: 'flex-start',
    paddingHorizontal: theme.spacing.sm,
    paddingVertical: 2,
    borderRadius: theme.radius.sm,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
}));
