import { View } from 'react-native';
import { radius, useColors } from '@/theme/tokens';
import { Text } from '@/ui';

/** Stands in for the QR image until a QR package is added; the 6-digit code beside it always works. */
export function QrPlaceholder({ value, size }: { value: string; size: number }) {
  const colors = useColors();
  return (
    <View
      accessibilityLabel="QR code"
      testID="qr-code"
      accessibilityHint={value ? 'Scan with the iPhone app' : undefined}
      style={{
        width: size,
        height: size,
        borderRadius: radius.md,
        borderWidth: 1,
        borderColor: colors.line,
        alignItems: 'center',
        justifyContent: 'center',
      }}
    >
      <Text tone="muted" variant="caption">
        QR code
      </Text>
    </View>
  );
}
