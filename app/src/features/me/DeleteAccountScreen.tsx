// Me > Delete account: says what will happen, then needs a fresh passkey before anything is sent.
// DELETE /me wants a passkey assertion in its body (docs/07), which only the web can produce. The phone explains where to do it.
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { useDeleteMe } from '@/api/generated/account/account';
import { requestLoginOptions } from '@/features/auth/api';
import { getPasskeys } from '@/features/auth/services';
import { ApiError } from '@/lib/http';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';
import { clearLocalData } from './localData';

export const membersMessage =
  'Your library still has other members. Remove them on the Members page first, then delete your account.';

export function DeleteAccountScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const deleteMe = useDeleteMe();
  const [message, setMessage] = useState<string | null>(null);

  const confirm = useMutation({
    mutationFn: async () => {
      // The fresh sign-in comes first: cancelling the passkey prompt sends nothing.
      const passkeys = getPasskeys();
      const { options } = await requestLoginOptions();
      const credential = await passkeys.get(options);
      await deleteMe.mutateAsync({ data: { credential } });
    },
    onSuccess: async () => {
      await clearLocalData(queryClient);
      router.replace('/login');
    },
    onError: (e) => setMessage(e instanceof ApiError && e.status === 409 ? membersMessage : friendlyError(e)),
  });

  return (
    <Screen title="Delete account" subtitle="Delete your library and photos. This can't be undone.">
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
          <Text tone="muted">You&apos;ll confirm with your passkey before anything is deleted.</Text>
          <Button
            label="Confirm with passkey and delete account"
            variant="danger"
            busy={confirm.isPending}
            onPress={() => {
              setMessage(null);
              confirm.mutate();
            }}
          />
        </>
      ) : (
        <Card>
          <Text>To delete your account, open CollectionMaxxing in a browser on your computer and go to Me, then Delete account. It needs your passkey, which the iPhone app can&apos;t use.</Text>
        </Card>
      )}
      <Button label="Keep my account" variant="secondary" onPress={() => router.back()} />
    </Screen>
  );
}
