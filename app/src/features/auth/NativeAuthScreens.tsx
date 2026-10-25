// iPhone Sign in screen (Auth0's browser sheet). Web has its own in ./WebAuthScreens.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import { getNativeAuth } from '@/lib/auth/session';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text } from '@/ui';
import { registerThisInstall } from './appSignIn';

export const signInFailedCopy = "Sign-in didn't finish. Try again.";

export function SignInScreen() {
  const router = useRouter();
  const colors = useColors();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const signIn = async () => {
    setBusy(true);
    setError(null);
    try {
      await getNativeAuth().signIn();
    } catch {
      // Backing out of the sheet lands here too: nothing to explain beyond trying again.
      setError(signInFailedCopy);
      setBusy(false);
      return;
    }
    // The first call made the account; with no library yet, the library screen sends it on to Welcome.
    await registerThisInstall();
    router.replace('/library');
  };

  return (
    <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title="CollectionMaxxing" titleVariant="display" showTitleOnPhone subtitle="Sign in to your library.">
        <Card>
          {error ? (
            <Text tone="danger" accessibilityRole="alert">
              {error}
            </Text>
          ) : null}
          <Button label="Sign in" busy={busy} onPress={signIn} />
        </Card>
      </Screen>
    </SafeAreaView>
  );
}
