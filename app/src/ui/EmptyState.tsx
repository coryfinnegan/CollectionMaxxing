import { View } from 'react-native';
import { spacing } from '@/theme/tokens';
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
    <View style={{ alignItems: 'center', gap: spacing.md, paddingVertical: spacing.xxl }}>
      {animation ? <LottieSpot name={animation} loop testID="empty-spot" /> : null}
      <Text variant="heading" accessibilityRole="header" aria-level={2} style={{ textAlign: 'center' }}>
        {title}
      </Text>
      {body ? (
        <Text tone="muted" style={{ textAlign: 'center' }}>
          {body}
        </Text>
      ) : null}
      {actionLabel && onAction ? <Button label={actionLabel} onPress={onAction} /> : null}
    </View>
  );
}
