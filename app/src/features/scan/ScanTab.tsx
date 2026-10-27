// The Scan tab: the camera, immediately. The first run shows a one-time coach overlay over the viewfinder; after
// that the scanner opens with no intermediate taps. Manual add lives in the control row; waiting scans open from the Review bar.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { Platform, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { EmptyState, Screen } from '@/ui';
import { ScanCoach } from './scanner/ScanCoach';
import { dismissScanCoach, hasSeenScanCoach } from './scanner/scanEntry';
import { ScannerRoute } from './scanner/ScannerRoute';

export function ScanTab() {
  if (Platform.OS === 'web') {
    return <WebScanTab />;
  }
  return <PhoneScanTab />;
}

function WebScanTab() {
  const { theme } = useUnistyles();
  // Plain object: SafeAreaView lives outside the babel-processed tree, so a
  // themed sheet value would arrive unstyled on web (#305).
  const safe = { flex: 1, backgroundColor: theme.colors.ground };
  return (
    <SafeAreaView testID="scan-tab-safe-area" edges={['left', 'right']} style={safe}>
      <Screen title="Scan">
        <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes and shelves." />
      </Screen>
    </SafeAreaView>
  );
}

function PhoneScanTab() {
  const { libraryId } = useActiveLibraryId();
  const [coached, setCoached] = useState(hasSeenScanCoach);
  return (
    <View style={styles.fill}>
      <ScannerRoute showClose={false} />
      {!coached && libraryId ? (
        <ScanCoach
          onDone={() => {
            dismissScanCoach();
            setCoached(true);
          }}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1 },
});
