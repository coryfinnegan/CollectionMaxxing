// The Scan tab: one big way into the scanner, plus a nudge when a scan is waiting for Review.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRouter } from 'expo-router';
import { Platform } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useUnistyles } from 'react-native-unistyles';
import { useStore } from 'zustand';
import { Button, Card, EmptyState, Screen, Text } from '@/ui';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { summaryLine } from './cards/copy';
import { getTrayStore } from './tray/storage';

export function ScanTab() {
  const { theme } = useUnistyles();
  // Plain object: SafeAreaView lives outside the babel-processed tree, so a
  // themed sheet value would arrive unstyled on web (#305).
  const safe = { flex: 1, backgroundColor: theme.colors.ground };
  return (
    <SafeAreaView testID="scan-tab-safe-area" edges={['left', 'right']} style={safe}>
      {Platform.OS === 'web' ? (
        <Screen title="Scan">
          <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes." />
        </Screen>
      ) : (
        <PhoneScanTab />
      )}
    </SafeAreaView>
  );
}

function PhoneScanTab() {
  const router = useRouter();
  const { libraryId } = useActiveLibraryId();
  return (
    <Screen title="Scan" showTitleOnPhone subtitle="Point the camera at barcodes. Each one is read as soon as it is in view.">
      <Button label="Open scanner" onPress={() => router.push('/scanner')} />
      <Button label="Add it by hand" variant="secondary" onPress={() => router.push({ pathname: '/library', params: { add: '1' } })} />
      {libraryId ? <WaitingCard libraryId={libraryId} /> : null}
    </Screen>
  );
}

/** The nudge counts this library's tray only: another library's waiting scan is not yours to review here. */
function WaitingCard({ libraryId }: { libraryId: string }) {
  const router = useRouter();
  const items = useStore(getTrayStore(libraryId), (s) => s.lines.reduce((sum, l) => sum + l.copies, 0));
  if (items === 0) return null;
  return (
    <Card>
      <Text variant="label">{`Waiting from your last scan: ${summaryLine(items, 0)}`}</Text>
      <Button label="Review" variant="secondary" onPress={() => router.push('/review')} />
    </Card>
  );
}
