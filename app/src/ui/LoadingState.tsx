// Shared loading treatment: a spinner with an optional label, laid out like EmptyState so
// loading branches read as designed states rather than afterthoughts (#270).
import { ActivityIndicator, View } from 'react-native';
import { spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type LoadingStateProps = {
  label?: string;
  /** Announce status-like loading (e.g. export progress) to screen readers. Plain spinners stay quiet. */
  alert?: boolean;
};

export function LoadingState({ label, alert = false }: LoadingStateProps) {
  const colors = useColors();
  return (
    <View testID="loading-state" style={{ alignItems: 'center', gap: spacing.md, paddingVertical: spacing.xxl }}>
      <ActivityIndicator size="large" color={colors.scan} accessibilityLabel={label ?? 'Loading'} />
      {label ? (
        <Text tone="muted" accessibilityRole={alert ? 'alert' : undefined} style={{ textAlign: 'center' }}>
          {label}
        </Text>
      ) : null}
    </View>
  );
}
