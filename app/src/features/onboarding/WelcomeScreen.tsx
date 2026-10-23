// Where an account with no libraries lands: create one, join one, or use another account (docs/15 "The new flow").
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { useGetMe } from '@/api/generated/account/account';
import { listSignInMethods, logout } from '@/features/auth/api';
import { navigateTo } from '@/features/auth/navigation';
import { clearLocalData } from '@/features/me/localData';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';
import { providerLabel } from './copy';

/** `GET /me` does not carry a display name yet; read it when it does so the greeting can use it. */
export function useAccountName(): string | undefined {
  const me = useGetMe({ query: { retry: false } });
  const data = me.data?.status === 200 ? (me.data.data as { displayName?: string | null }) : undefined;
  return data?.displayName?.trim() || undefined;
}

export function useSignedInWith(): string | undefined {
  const methods = useQuery({ queryKey: ['onboarding', 'sign-in-method'], queryFn: listSignInMethods, retry: false });
  const current = methods.data?.find((m) => m.isCurrent);
  if (!current) return undefined;
  const provider = providerLabel[current.provider];
  if (!provider) return undefined;
  return current.email ? `You're signed in with ${provider} as ${current.email}.` : `You're signed in with ${provider}.`;
}

/** Ends this browser's Auth0 session too, so the account picker shows again. */
export function useUseAnotherAccount() {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const { logoutUrl } = await logout();
      await clearLocalData(queryClient);
      navigateTo(logoutUrl);
    } catch (e) {
      setError(friendlyError(e));
      setBusy(false);
    }
  };
  return { run, busy, error };
}

export function WelcomeScreen() {
  const router = useRouter();
  const name = useAccountName();
  const signedInWith = useSignedInWith();
  const another = useUseAnotherAccount();
  return (
    <Screen title={name ? `Welcome, ${name}.` : 'Welcome.'} subtitle={signedInWith}>
      <Card>
        <Button label="Create your library" onPress={() => router.push('/welcome/create')} />
        <Button label="Join a library" variant="secondary" onPress={() => router.push('/welcome/join')} />
        <Button label="Use another account" variant="secondary" busy={another.busy} onPress={another.run} />
        {another.error ? (
          <Text tone="danger" accessibilityRole="alert">
            {another.error}
          </Text>
        ) : null}
      </Card>
    </Screen>
  );
}
