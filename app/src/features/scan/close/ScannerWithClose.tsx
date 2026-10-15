// The /scanner route with its close behaviour: Close asks Review / Keep for later / Discard when items are waiting.
import { useRouter } from 'expo-router';
import { View } from 'react-native';
import { ScannerRoute } from '../scanner/ScannerRoute';
import { CloseScannerSheet } from './CloseScannerSheet';
import { useCloseScanner } from './useCloseScanner';

export function ScannerWithClose() {
  const router = useRouter();
  const close = useCloseScanner({ onLeave: () => router.back(), onReview: () => router.replace('/review') });
  return (
    <View style={{ flex: 1 }}>
      <ScannerRoute onClose={close.request} />
      <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
    </View>
  );
}
