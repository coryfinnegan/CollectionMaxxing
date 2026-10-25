// The /scanner route with its close behaviour: Close asks Review / Keep for later / Discard when items are waiting.
import { useRouter } from 'expo-router';
import { View } from 'react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { ScannerRoute } from '../scanner/ScannerRoute';
import { CloseScannerSheet } from './CloseScannerSheet';
import { useCloseScanner } from './useCloseScanner';

export function ScannerWithClose() {
  const router = useRouter();
  const { libraryId } = useActiveLibraryId();
  const close = useCloseScanner({ libraryId, onLeave: () => router.back(), onReview: () => router.replace('/review') });
  return (
    <View style={{ flex: 1 }}>
      <ScannerRoute onClose={close.request} />
      <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
    </View>
  );
}
