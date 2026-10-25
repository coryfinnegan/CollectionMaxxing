// The /scanner route: finds the library, builds the real camera source (plus the fake one in dev builds) and
// opens the scanner. On the web there is no scanner (docs/06 W2), so the camera is never created or imported.
import { useRouter } from 'expo-router';
import { useMemo, type ReactNode } from 'react';
import { Platform } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { useColors } from '@/theme/tokens';
import { Button, EmptyState, LoadingState, Screen } from '@/ui';
import { createDevicePipelineDeps } from '../pipeline/device';
import { useAutoSnapConfig } from '../pipeline/useAutoSnapConfig';
import { LazyCameraPreview } from './LazyCameraPreview';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource, VisionCameraSource } from './ScannerSource';

export function ScannerRoute({ onClose }: { onClose?: () => void }) {
  if (Platform.OS === 'web') {
    return (
      <RouteFallback onClose={onClose}>
        <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes. You can still browse and edit your library here." />
      </RouteFallback>
    );
  }
  return <PhoneScanner onClose={onClose} />;
}

/** The scanner's waiting room (web, or the library isn't ready): safe-area aware, with a way back out of the modal. */
function RouteFallback({ onClose, children }: { onClose?: () => void; children: ReactNode }) {
  const router = useRouter();
  const colors = useColors();
  return (
    <SafeAreaView testID="scanner-fallback-safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen>
        <Button label="Close" variant="secondary" onPress={onClose ?? (() => router.back())} />
        {children}
      </Screen>
    </SafeAreaView>
  );
}

function PhoneScanner({ onClose }: { onClose?: () => void }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  const camera = useMemo(() => new VisionCameraSource(), []);
  const devSource = useMemo(() => (__DEV__ ? new FakeScannerSource() : undefined), []);
  const pipeline = useMemo(() => (libraryId ? createDevicePipelineDeps(libraryId) : undefined), [libraryId]);
  const autoSnapConfig = useAutoSnapConfig();
  if (!libraryId) {
    return (
      <RouteFallback onClose={onClose}>
        {isLoading ? (
          <LoadingState label="Getting your library ready…" />
        ) : (
          <EmptyState title="No library found." body="Pair this iPhone again." />
        )}
      </RouteFallback>
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
