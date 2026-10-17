// The /scanner route: finds the library, builds the real camera source (plus the fake one in dev builds) and
// opens the scanner. On the web there is no scanner (docs/06 W2), so the camera is never created or imported.
import { useMemo } from 'react';
import { Platform } from 'react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { EmptyState, Screen, Text } from '@/ui';
import { createDevicePipelineDeps } from '../pipeline/device';
import { useAutoSnapConfig } from '../pipeline/useAutoSnapConfig';
import { LazyCameraPreview } from './LazyCameraPreview';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource, VisionCameraSource } from './ScannerSource';

export function ScannerRoute({ onClose }: { onClose?: () => void }) {
  if (Platform.OS === 'web') {
    return (
      <Screen title="Scanner">
        <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes. You can still browse and edit your library here." />
      </Screen>
    );
  }
  return <PhoneScanner onClose={onClose} />;
}

function PhoneScanner({ onClose }: { onClose?: () => void }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  const camera = useMemo(() => new VisionCameraSource(), []);
  const devSource = useMemo(() => (__DEV__ ? new FakeScannerSource() : undefined), []);
  const pipeline = useMemo(() => (libraryId ? createDevicePipelineDeps(libraryId) : undefined), [libraryId]);
  const autoSnapConfig = useAutoSnapConfig();
  if (!libraryId) {
    return (
      <Screen title="Scanner">
        <Text tone="muted">{isLoading ? 'Getting your library ready…' : 'No library found. Pair this iPhone again.'}</Text>
      </Screen>
    );
  }
  return (
    <ScannerScreen
      libraryId={libraryId}
      source={camera}
      preview={<LazyCameraPreview source={camera} />}
      devSource={devSource}
      onClose={onClose}
      pipeline={pipeline}
      autoSnapConfig={autoSnapConfig}
    />
  );
}
