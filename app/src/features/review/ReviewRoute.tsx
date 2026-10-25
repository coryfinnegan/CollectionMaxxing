import { useRouter } from 'expo-router';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { getTrayStore, useTrayValue } from '@/features/scan/tray/storage';
import { useColors } from '@/theme/tokens';
import { LoadingState, Screen } from '@/ui';
import { ReviewScreen } from './ReviewScreen';

export function ReviewRoute() {
  const router = useRouter();
  const colors = useColors();
  const { libraryId, isLoading } = useActiveLibraryId();
  // The session of THIS library's tray: a session stored while scanning another library never pairs with this one.
  const sessionId = useTrayValue(libraryId ? getTrayStore(libraryId) : undefined, (s) => s.sessionId, null);
  return (
    <SafeAreaView testID="review-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
      {isLoading || !libraryId ? (
        <Screen>
          <LoadingState label="Loading…" />
        </Screen>
      ) : (
        <ReviewScreen libraryId={libraryId} sessionId={sessionId} onBack={() => router.back()} />
      )}
    </SafeAreaView>
  );
}
