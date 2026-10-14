// The Scan tab: one big way into the scanner, plus a nudge when a scan is waiting for Review.
import { useRouter } from 'expo-router';
import { Platform } from 'react-native';
import { useStore } from 'zustand';
import { Button, Card, EmptyState, Screen, Text } from '@/ui';
import { summaryLine } from './cards/copy';
import { getTrayStore } from './tray/storage';

export function ScanTab() {
  if (Platform.OS === 'web') {
    return (
      <Screen title="Scan">
        <EmptyState title="Scanning is on the iPhone app" body="Open CollectionMaxxing on your iPhone to scan barcodes." />
      </Screen>
    );
  }
  return <PhoneScanTab />;
}

function PhoneScanTab() {
  const router = useRouter();
  const items = useStore(getTrayStore(), (s) => s.lines.reduce((sum, l) => sum + l.copies, 0));
  return (
    <Screen title="Scan" subtitle="Point the camera at barcodes. Each one is read as soon as it is in view.">
      <Button label="Open scanner" onPress={() => router.push('/scanner')} />
      {items > 0 ? (
        <Card>
          <Text variant="label">{`Waiting from your last scan: ${summaryLine(items, 0)}`}</Text>
          <Button label="Review" variant="secondary" onPress={() => router.push('/review')} />
        </Card>
      ) : null}
    </Screen>
  );
}
