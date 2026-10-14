// Web-only screens: first-run setup, passkey sign-in and joining by invite.
import { useQuery } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { getInvitePreview } from './api';
import { type FlowContext, ceremonyMessage, joinWithInvite, setUpOwner, signInWithPasskey } from './flows';
import { getPasskeys } from './services';
import { Button, Card, Screen, Text, TextField } from '@/ui';

/** Runs a ceremony, keeping busy and error state, and goes to the library on success. */
function useCeremony(context: FlowContext) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const unsupported = !getPasskeys().isSupported();

  const run = async (ceremony: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await ceremony();
      router.replace('/library');
    } catch (e) {
      setError(ceremonyMessage(e, context));
      setBusy(false);
    }
  };
  return { busy, error, run, unsupported };
}

const unsupportedCopy =
  "This browser can't use passkeys. Open CollectionMaxxing in a current version of Edge, Chrome, Safari or Firefox.";

export function SetupScreen() {
  const { busy, error, run, unsupported } = useCeremony('setup');
  const [code, setCode] = useState('');
  const [displayName, setDisplayName] = useState('');
  const ready = code.trim() !== '' && displayName.trim() !== '' && !unsupported;

  return (
    <Screen title="Set up CollectionMaxxing" subtitle="Create your account on this computer.">
      <Card>
        <Text tone="muted">
          Enter the setup code from your server. It is in the server log, and in secrets/setup-code.txt on your computer.
        </Text>
        <TextField label="Setup code" value={code} onChangeText={setCode} autoCapitalize="none" autoCorrect={false} />
        <TextField label="Your name" value={displayName} onChangeText={setDisplayName} autoComplete="name" />
        {unsupported ? (
          <Text tone="danger" accessibilityRole="alert">
            {unsupportedCopy}
          </Text>
        ) : null}
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button
          label="Create passkey"
          busy={busy}
          disabled={!ready}
          onPress={() => run(() => setUpOwner(getPasskeys(), { code, displayName }))}
        />
      </Card>
    </Screen>
  );
}

export function LoginScreen() {
  const router = useRouter();
  const { busy, error, run, unsupported } = useCeremony('login');
  return (
    <Screen title="CollectionMaxxing" subtitle="Sign in to your library.">
      <Card>
        {unsupported ? (
          <Text tone="danger" accessibilityRole="alert">
            {unsupportedCopy}
          </Text>
        ) : null}
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button
          label="Sign in with a passkey"
          busy={busy}
          disabled={unsupported}
          onPress={() => run(() => signInWithPasskey(getPasskeys()))}
        />
        <Button label="First time here? Set up CollectionMaxxing" variant="secondary" onPress={() => router.push('/setup')} />
      </Card>
    </Screen>
  );
}

const roleLabel = { editor: 'Editor', viewer: 'Viewer' } as const;

export function JoinScreen({ token }: { token: string }) {
  const { busy, error, run, unsupported } = useCeremony('join');
  const [displayName, setDisplayName] = useState('');
  const invite = useQuery({ queryKey: ['invite', token], queryFn: () => getInvitePreview(token), retry: false });

  if (invite.isError) {
    return (
      <Screen title="Join a library">
        <Card>
          <Text accessibilityRole="alert" tone="danger">
            {ceremonyMessage(invite.error, 'join')}
          </Text>
        </Card>
      </Screen>
    );
  }

  const title = invite.data
    ? `Join ${invite.data.ownerName}'s library as ${roleLabel[invite.data.role]}`
    : 'Join a library';
  return (
    <Screen title={title} subtitle="Create a passkey to join.">
      <Card>
        <TextField label="Your name" value={displayName} onChangeText={setDisplayName} autoComplete="name" />
        {unsupported ? (
          <Text tone="danger" accessibilityRole="alert">
            {unsupportedCopy}
          </Text>
        ) : null}
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button
          label="Create passkey"
          busy={busy}
          disabled={!invite.data || displayName.trim() === '' || unsupported}
          onPress={() => run(() => joinWithInvite(getPasskeys(), { token, displayName }))}
        />
      </Card>
    </Screen>
  );
}
