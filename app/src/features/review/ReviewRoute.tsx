import { useRouter } from 'expo-router';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useStore } from 'zustand';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { getTrayStore } from '@/features/scan/tray/storage';
import { useColors } from '@/theme/tokens';
import { LoadingState, Screen } from '@/ui';
import { ReviewScreen } from './ReviewScreen';

export function ReviewRoute() {
  const router = useRouter();
  const colors = useColors();
  const { libraryId, isLoading } = useActiveLibraryId();
  const sessionId = useStore(getTrayStore(), (s) => s.sessionId);
  return (
    <SafeAreaView testID="review-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
      {isLoading || !libraryId ? (
        <Screen title="Review">
          <LoadingState label="Loading…" />
        </Screen>
      ) : (
        <ReviewScreen libraryId={libraryId} sessionId={sessionId} onBack={() => router.back()} />
      )}
    </SafeAreaView>
  );
}
