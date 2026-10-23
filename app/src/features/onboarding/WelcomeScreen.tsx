// Where an account with no libraries lands: create one, join one, or use another account (docs/15 "The new flow").
import { useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { useGetMe } from '@/api/generated/account/account';
import { logout } from '@/features/auth/api';
import { navigateTo } from '@/features/auth/navigation';
import { clearLocalData } from '@/features/me/localData';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';

/** The signed-in account from `GET /api/v1/me`: its name and, when it has one, its email. */
export function useAccount(): { name: string; email: string | null } | undefined {
  const me = useGetMe({ query: { retry: false } });
  return me.data?.status === 200 ? { name: me.data.data.displayName, email: me.data.data.email } : undefined;
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
  const account = useAccount();
  const another = useUseAnotherAccount();
  return (
    <Screen title={account ? `Welcome, ${account.name}.` : 'Welcome.'} subtitle={account?.email ? `You're signed in as ${account.email}.` : undefined}>
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
