// The /scanner route: finds the library, builds the real camera source (plus the fake one in dev builds) and
// opens the scanner. On the web there is no scanner (docs/06 W2), so the camera is never created or imported.
import { useRouter } from 'expo-router';
import { useCallback, useMemo, type ReactNode } from 'react';
import { Platform } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useStore } from 'zustand';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { LibraryLoading } from '@/features/onboarding/LibraryLoading';
import { boundScanStore } from '@/features/review/boundScan';
import { useColors } from '@/theme/tokens';
import { Button, EmptyState, Screen } from '@/ui';
import { createDevicePipelineDeps } from '../pipeline/device';
import { useAutoSnapConfig } from '../pipeline/useAutoSnapConfig';
import { LazyCameraPreview } from './LazyCameraPreview';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource, VisionCameraSource } from './ScannerSource';
import { useScreenActive } from './useScreenActive';

export function ScannerRoute({ onClose, showClose = true }: { onClose?: () => void; showClose?: boolean }) {
  if (Platform.OS === 'web') {
    return (
      <RouteFallback onClose={onClose} showClose={showClose}>
        <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes and shelves. You can still browse and edit your library here." />
      </RouteFallback>
    );
  }
  return <PhoneScanner onClose={onClose} showClose={showClose} />;
}

/** The scanner's waiting room (web, or the library isn't ready): safe-area aware, with a way back out of the modal. */
function RouteFallback({ onClose, showClose, children }: { onClose?: () => void; showClose: boolean; children: ReactNode }) {
  const router = useRouter();
  const colors = useColors();
  return (
    <SafeAreaView testID="scanner-fallback-safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen>
        {showClose ? <Button label="Close" variant="secondary" onPress={onClose ?? (() => router.back())} /> : null}
        {children}
      </Screen>
    </SafeAreaView>
  );
}

function PhoneScanner({ onClose, showClose }: { onClose?: () => void; showClose: boolean }) {
  const router = useRouter();
  const { libraryId, isLoading, isError } = useActiveLibraryId();
  const binding = useStore(boundScanStore, (s) => s.binding);
  const camera = useMemo(() => new VisionCameraSource(), []);
  const devSource = useMemo(() => (__DEV__ ? new FakeScannerSource() : undefined), []);
  const pipeline = useMemo(() => (libraryId ? createDevicePipelineDeps(libraryId) : undefined), [libraryId]);
  const autoSnapConfig = useAutoSnapConfig();
  // The Scan tab stays mounted behind the other tabs: the camera, barcodes and auto-snap run only while this screen is showing.
  const active = useScreenActive();
  const leaveBound = useCallback(() => {
    boundScanStore.getState().clear();
    router.back();
  }, [router]);
  const boundScan = useMemo(
    () => (binding ? { ...binding, onDone: leaveBound, onCancel: leaveBound } : undefined),
    [binding, leaveBound],
  );
  if (!libraryId) {
    return (
      <RouteFallback onClose={onClose} showClose={showClose}>
        {isLoading || isError ? (
          <LibraryLoading label="Getting your library ready…" />
        ) : (
          <EmptyState title="No library found." body="Create or join a library in Settings, then come back to scan." />
        )}
      </RouteFallback>
    );
  }
  return (
    <ScannerScreen
      libraryId={libraryId}
      source={camera}
      active={active}
      preview={<LazyCameraPreview source={camera} active={active} />}
      devSource={devSource}
      onClose={onClose}
      showClose={showClose}
      pipeline={pipeline}
      autoSnapConfig={autoSnapConfig}
      boundScan={boundScan}
    />
  );
}
