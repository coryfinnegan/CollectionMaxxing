// Web-only screens: sign-in, first-run setup, joining by invite, and "this account isn't part of a library".
// Signing in is a navigation to the server (which hands off to Auth0); nothing here handles a password or a token.
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useState } from 'react';
import { useGetInvitePreview } from '@/api/generated/invites/invites';
import type { LibraryRole } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text, TextField } from '@/ui';
import { logout, startSetup } from './api';
import { loginUrl, navigateTo, safeReturnUrl, signInErrorMessage } from './navigation';

const setupMessages: Record<number, string> = {
  400: "That setup code isn't right. Check the code on your computer and try again.",
  404: 'Setup is already finished. Sign in instead.',
  410: 'That setup code has expired. Restart the server on your computer to get a new one.',
};

export function setupMessage(error: unknown): string {
  return error instanceof ApiError && setupMessages[error.status] ? setupMessages[error.status] : friendlyError(error);
}

function ErrorText({ children }: { children: string | null }) {
  return children ? (
    <Text tone="danger" accessibilityRole="alert">
      {children}
    </Text>
  ) : null;
}

export function SetupScreen() {
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const { next } = await startSetup(code.trim());
      // The next step is always our own sign-in route.
      if (!next.startsWith('/account/login')) throw new Error('Unexpected next step');
      navigateTo(next);
    } catch (e) {
      setError(setupMessage(e));
      setBusy(false);
    }
  };

  return (
    <Screen title="Set up CollectionMaxxing" subtitle="Create your account on this computer.">
      <Card>
        <Text tone="muted">
          Enter the setup code from your server. It is in the server log, and in secrets/setup-code.txt on your computer. Then you&apos;ll sign in
          with Google or Apple.
        </Text>
        <TextField label="Setup code" value={code} onChangeText={setCode} autoCapitalize="none" autoCorrect={false} />
        <ErrorText>{error}</ErrorText>
        <Button label="Continue" busy={busy} disabled={code.trim() === ''} onPress={submit} />
      </Card>
    </Screen>
  );
}

export function LoginScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ error?: string; returnUrl?: string }>();
  const returnUrl = safeReturnUrl(params.returnUrl);
  return (
    <Screen title="CollectionMaxxing" subtitle="Sign in to your library.">
      <Card>
        <ErrorText>{signInErrorMessage(params.error)}</ErrorText>
        <Button label="Sign in" onPress={() => navigateTo(loginUrl({ returnUrl }))} />
        <Button label="First time here? Set up CollectionMaxxing" variant="secondary" onPress={() => router.push('/setup')} />
      </Card>
    </Screen>
  );
}

const roleLabel: Record<LibraryRole, string> = { owner: 'Owner', editor: 'Editor', viewer: 'Viewer' };

/** One message for a bad, used or expired invite: the page must not say which. */
const invalidInviteCopy = "This invite link isn't valid anymore. Ask for a new one.";

export function JoinScreen({ token }: { token: string }) {
  const preview = useGetInvitePreview(token, { query: { retry: false } });
  const invite = preview.data?.status === 200 ? preview.data.data : undefined;

  if (preview.isError) {
    return (
      <Screen title="Join a library">
        <Card>
          <Text accessibilityRole="alert" tone="danger">
            {invalidInviteCopy}
          </Text>
        </Card>
      </Screen>
    );
  }

  const title = invite ? `Join ${invite.ownerName}'s library as ${roleLabel[invite.role]}` : 'Join a library';
  return (
    <Screen title={title} subtitle="Sign in with Google or Apple to join.">
      <Card>
        <Button label="Sign in to join" disabled={!invite} onPress={() => navigateTo(loginUrl({ invite: token }))} />
      </Card>
    </Screen>
  );
}

/** Where a sign-in lands when the account isn't linked to anyone here. No session exists yet. */
export function NotLinkedScreen() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Ending the Auth0 session is what lets the next sign-in offer a different account.
  const leave = async () => {
    setBusy(true);
    setError(null);
    try {
      const { logoutUrl } = await logout();
      navigateTo(logoutUrl);
    } catch (e) {
      setError(friendlyError(e));
      setBusy(false);
    }
  };

  return (
    <Screen title="Not part of a library">
      <Card>
        <Text>This account isn&apos;t part of a library. Ask the owner for an invite.</Text>
        <ErrorText>{error}</ErrorText>
        <Button label="Try another account" busy={busy} onPress={leave} />
        <Button label="Sign out" variant="secondary" busy={busy} onPress={leave} />
      </Card>
    </Screen>
  );
}
