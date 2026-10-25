// Shared loading treatment: a spinner with an optional label, laid out like EmptyState so
// loading branches read as designed states rather than afterthoughts (#270).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { ActivityIndicator, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { Text } from './Text';

type LoadingStateProps = {
  label?: string;
  /** Announce status-like loading (e.g. export progress) to screen readers. Plain spinners stay quiet. */
  alert?: boolean;
};

export function LoadingState({ label, alert = false }: LoadingStateProps) {
  const { theme } = useUnistyles();
  return (
    <View testID="loading-state" style={styles.root}>
      <ActivityIndicator size="large" color={theme.colors.accent} accessibilityLabel={label ?? 'Loading'} />
      {label ? (
        <Text tone="muted" accessibilityRole={alert ? 'alert' : undefined} style={styles.centered}>
          {label}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { alignItems: 'center', gap: theme.spacing.md, paddingVertical: theme.spacing.xxl },
  centered: { textAlign: 'center' },
}));
