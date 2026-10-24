// The Scan tab: one big way into the scanner, plus a nudge when a scan is waiting for Review.
import { useRouter } from 'expo-router';
import { Platform } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useStore } from 'zustand';
import { useColors } from '@/theme/tokens';
import { Button, Card, EmptyState, Screen, Text } from '@/ui';
import { summaryLine } from './cards/copy';
import { getTrayStore } from './tray/storage';

export function ScanTab() {
  const colors = useColors();
  return (
    <SafeAreaView testID="scan-tab-safe-area" edges={['left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
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
  const items = useStore(getTrayStore(), (s) => s.lines.reduce((sum, l) => sum + l.copies, 0));
  return (
    <Screen title="Scan" subtitle="Point the camera at barcodes. Each one is read as soon as it is in view.">
      <Button label="Open scanner" onPress={() => router.push('/scanner')} />
      <Button label="Add an item by hand" variant="secondary" onPress={() => router.push({ pathname: '/library', params: { add: '1' } })} />
      {items > 0 ? (
        <Card>
          <Text variant="label">{`Waiting from your last scan: ${summaryLine(items, 0)}`}</Text>
          <Button label="Review" variant="secondary" onPress={() => router.push('/review')} />
        </Card>
      ) : null}
    </Screen>
  );
}
