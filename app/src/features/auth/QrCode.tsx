import { View } from 'react-native';
import QRCode from 'react-native-qrcode-svg';
import { QrPlaceholder } from './QrPlaceholder';

/** The pairing QR. An empty value (no code issued yet) shows the placeholder. */
export function QrCode({ value, size }: { value: string; size: number }) {
  if (!value) return <QrPlaceholder value={value} size={size} />;
  return (
    <View accessibilityRole="image" accessibilityLabel="QR code" testID="qr-code" accessibilityHint="Scan with the iPhone app">
      <QRCode value={value} size={size} ecl="M" quietZone={8} backgroundColor="#ffffff" color="#000000" />
    </View>
  );
}
