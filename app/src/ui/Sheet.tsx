import type { ReactNode } from 'react';
import { View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button } from './Button';
import { Text } from './Text';

type SheetProps = { title: string; onClose: () => void; children?: ReactNode };

/** A panel that opens inline above the content, with a title and a Close button. */
export function Sheet({ title, onClose, children }: SheetProps) {
  const colors = useColors();
  return (
    <View
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
    </View>
  );
}
