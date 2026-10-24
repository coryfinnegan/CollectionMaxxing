// `/join/{token}`: signed out it offers "Sign in to join" and comes back to this page; signed in it offers "Join".
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useGetMe } from '@/api/generated/account/account';
import { useGetInvitePreview } from '@/api/generated/invites/invites';
import { loginUrl, navigateTo } from '@/features/auth/navigation';
import { ApiError } from '@/lib/http';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text } from '@/ui';
import { inviteError, invalidInviteCopy } from './copy';
import { useAcceptInvite } from './hooks';
import { roleLabel } from './JoinLibraryScreen';

export function JoinInviteScreen({ token }: { token: string }) {
  const router = useRouter();
  const colors = useColors();
  const preview = useGetInvitePreview(token, { query: { retry: false } });
  const me = useGetMe({ query: { retry: false } });
  const accept = useAcceptInvite();
  const [error, setError] = useState<string | null>(null);
  const invite = preview.data?.status === 200 ? preview.data.data : undefined;
  const signedOut = me.isError && me.error instanceof ApiError && me.error.status === 401;
  const signedIn = me.data?.status === 200;

  if (preview.isError) {
    return (
      <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
        <Screen title="Join a library">
          <Card>
            <Text accessibilityRole="alert" tone="danger">
              {invalidInviteCopy}
            </Text>
          </Card>
        </Screen>
      </SafeAreaView>
    );
  }

  const join = () => {
    setError(null);
    accept.mutate({ invite: token }, { onSuccess: () => router.replace('/library'), onError: (e) => setError(inviteError(e)) });
  };

  const title = invite ? `Join ${invite.ownerName}'s library as ${roleLabel[invite.role]}` : 'Join a library';
  return (
    <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title={title} subtitle={signedOut ? 'Sign in with Google or Apple to join.' : undefined}>
        <Card>
          {error ? (
            <Text accessibilityRole="alert" tone="danger">
              {error}
            </Text>
          ) : null}
          {signedIn ? (
            <Button label="Join" busy={accept.isPending} disabled={!invite} onPress={join} />
          ) : (
            <Button
              label="Sign in to join"
              disabled={!invite || !signedOut}
              onPress={() => navigateTo(loginUrl({ returnUrl: `/join/${encodeURIComponent(token)}` }))}
            />
          )}
        </Card>
      </Screen>
    </SafeAreaView>
  );
}
