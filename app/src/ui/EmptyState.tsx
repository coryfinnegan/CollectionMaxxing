import { View } from 'react-native';
import { spacing } from '@/theme/tokens';
import { Button } from './Button';
import { Text } from './Text';

type EmptyStateProps = {
  title: string;
  body?: string;
  actionLabel?: string;
  onAction?: () => void;
};

export function EmptyState({ title, body, actionLabel, onAction }: EmptyStateProps) {
  return (
    <View style={{ alignItems: 'center', gap: spacing.md, paddingVertical: spacing.xxl }}>
      <Text variant="heading" style={{ textAlign: 'center' }}>
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
