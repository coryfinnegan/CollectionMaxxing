// iPhone screens: Sign in (Auth0's browser sheet) and "this account isn't part of a library". Web has its own in ./WebAuthScreens.
import { useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { signOutThisDevice } from '@/features/me/unpair';
import { getNativeAuth } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';
import { registerThisInstall } from './appSignIn';

export const signInFailedCopy = "Sign-in didn't finish. Try again.";

export function SignInScreen() {
  const router = useRouter();
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
    // If the account is in no library, the HTTP client has already opened the not-linked page.
    await registerThisInstall();
    router.replace('/library');
  };

  return (
    <Screen title="CollectionMaxxing" subtitle="Sign in to your library.">
      <Card>
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label="Sign in" busy={busy} onPress={signIn} />
      </Card>
    </Screen>
  );
}

export function NativeNotLinkedScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const leave = async () => {
    setBusy(true);
    setError(null);
    try {
      await signOutThisDevice(queryClient);
      router.replace('/login');
    } catch (e) {
      setError(friendlyError(e));
      setBusy(false);
    }
  };

  return (
    <Screen title="Not part of a library">
      <Card>
        <Text>This account isn&apos;t part of a library. Ask the owner for an invite.</Text>
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label="Sign out" busy={busy} onPress={leave} />
      </Card>
    </Screen>
  );
}
