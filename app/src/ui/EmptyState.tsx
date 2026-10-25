// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Button } from './Button';
import { LottieSpot, type LottieSpotName } from './LottieSpot';
import { Text } from './Text';

type EmptyStateProps = {
  title: string;
  body?: string;
  actionLabel?: string;
  onAction?: () => void;
  /** A small looping spot above the words. Error states stay bare: no animation on data-critical paths. */
  animation?: Extract<LottieSpotName, 'empty-library' | 'no-results'>;
};

export function EmptyState({ title, body, actionLabel, onAction, animation }: EmptyStateProps) {
  return (
    <View style={styles.root}>
      {animation ? <LottieSpot name={animation} loop testID="empty-spot" /> : null}
      <Text variant="heading" accessibilityRole="header" aria-level={2} style={styles.centered}>
        {title}
      </Text>
      {body ? (
        <Text tone="muted" style={styles.centered}>
          {body}
        </Text>
      ) : null}
      {actionLabel && onAction ? <Button label={actionLabel} onPress={onAction} /> : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { alignItems: 'center', gap: theme.spacing.md, paddingVertical: theme.spacing.xxl },
  centered: { textAlign: 'center' },
}));
