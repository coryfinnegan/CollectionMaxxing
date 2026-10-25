// Me > Delete account: says what will happen, then deletes. DELETE /me needs a recent sign-in: if it isn't recent enough the server
// answers reauth_required and the HTTP client asks for a fresh sign-in first (web only). The phone explains where to do it.
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { ApiError, customFetch } from '@/lib/http';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';
import { BackButton } from './BackButton';
import { clearLocalData } from './localData';

export const membersMessage =
  'Your library still has other members. Remove them on the Members page first, then delete your account.';

export function DeleteAccountScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);

  const confirm = useMutation({
    mutationFn: () => customFetch('/api/v1/me', { method: 'DELETE' }),
    onSuccess: async () => {
      await clearLocalData(queryClient);
      router.replace('/login');
    },
    onError: (e) => setMessage(e instanceof ApiError && e.status === 409 ? membersMessage : friendlyError(e)),
  });

  return (
    <Screen title="Delete account" subtitle="Delete your library and photos. This can't be undone.">
      <BackButton />
      <Card>
        <Text variant="heading">What will happen</Text>
        <Text>Your account is signed out everywhere right away.</Text>
        <Text>Your library and photos are deleted within 30 days.</Text>
        <Text>This can&apos;t be undone. Export your data first if you want to keep a copy.</Text>
      </Card>
      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}
      {isWeb() ? (
        <>
          <Text variant="caption" tone="muted">You may be asked to sign in again before anything is deleted.</Text>
          <Button
            label="Delete my account"
            variant="danger"
            haptic="commit"
            busy={confirm.isPending}
            onPress={() => {
              setMessage(null);
              confirm.mutate();
            }}
          />
        </>
      ) : (
        <Card>
          <Text>To delete your account, open CollectionMaxxing in a browser on your computer and go to Me, then Delete account. It needs a fresh sign-in, which only the browser can do.</Text>
        </Card>
      )}
      <Button label="Keep my account" variant="secondary" onPress={() => router.back()} />
    </Screen>
  );
}
