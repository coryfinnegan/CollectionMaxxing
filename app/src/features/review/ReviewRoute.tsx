import { useRouter } from 'expo-router';
import { useStore } from 'zustand';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { getTrayStore } from '@/features/scan/tray/storage';
import { Screen, Text } from '@/ui';
import { ReviewScreen } from './ReviewScreen';

export function ReviewRoute() {
  const router = useRouter();
  const { libraryId, isLoading } = useActiveLibraryId();
  const sessionId = useStore(getTrayStore(), (s) => s.sessionId);
  if (isLoading || !libraryId) {
    return (
      <Screen title="Review">
        <Text tone="muted">Loading…</Text>
      </Screen>
    );
  }
  return <ReviewScreen libraryId={libraryId} sessionId={sessionId} onBack={() => router.back()} />;
}
