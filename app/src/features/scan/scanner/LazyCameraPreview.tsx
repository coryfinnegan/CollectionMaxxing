// Loads the camera only when the scanner opens, and shows a plain message if it cannot load (Expo Go, tests).
import { Component, lazy, Suspense, type ReactNode } from 'react';
import { StyleSheet, View } from 'react-native';
import { fixed, spacing } from '@/theme/tokens';
import { Text } from '@/ui';
import type { VisionCameraSource } from './ScannerSource';

// `require` (not `import()`) so Metro bundles it normally; the file is still only evaluated when the scanner opens.
// eslint-disable-next-line @typescript-eslint/no-require-imports
const CameraPreview = lazy(async () => ({ default: (require('./CameraPreview') as typeof import('./CameraPreview')).default }));

class CameraBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  render() {
    return this.state.failed ? (
      <View style={styles.message}>
        <Text style={styles.onCamera}>The camera isn&apos;t available in this build. Install the iPhone app to scan.</Text>
      </View>
    ) : (
      this.props.children
    );
  }
}

export function LazyCameraPreview({ source, active = true }: { source: VisionCameraSource; active?: boolean }) {
  return (
    <CameraBoundary>
      <Suspense fallback={null}>
        <CameraPreview source={source} active={active} />
      </Suspense>
    </CameraBoundary>
  );
}

const styles = StyleSheet.create({
  message: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, alignItems: 'center', justifyContent: 'center', padding: spacing.xl },
  onCamera: { color: fixed.white, textAlign: 'center' },
});
