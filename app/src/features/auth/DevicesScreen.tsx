// Me > Devices and browsers: the browsers you are signed in on, and your iPhones (docs/14 "Web").
// Browsers can be signed out one by one. iPhones are signed out together: each one signs in with Auth0 on its own.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { type SessionDto, listDevices, revokeBrowser, revokeWebSessions, signOutAllIphones } from './api';
import { Button, Card, ListRow, Screen, Text } from '@/ui';
import { BackButton } from '@/features/me/BackButton';

const devicesKey = ['me', 'devices'] as const;

export function formatLastSeen(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return 'Not seen yet';
  const minutes = Math.floor((now - new Date(iso).getTime()) / 60_000);
  if (minutes < 2) return 'Last seen just now';
  if (minutes < 60) return `Last seen ${minutes} minutes ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `Last seen ${hours} ${hours === 1 ? 'hour' : 'hours'} ago`;
  const days = Math.floor(hours / 24);
  return `Last seen ${days} ${days === 1 ? 'day' : 'days'} ago`;
}

type Confirming = { kind: 'browser'; session: SessionDto } | { kind: 'all-browsers' } | { kind: 'all-iphones' };

export function DevicesScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const devices = useQuery({ queryKey: devicesKey, queryFn: listDevices });
  const [confirming, setConfirming] = useState<Confirming | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const done = (leaves: boolean) => {
    setConfirming(null);
    // On the web this browser's own session may have just ended. Elsewhere: stay and refresh.
    if (leaves) router.replace('/login');
    else queryClient.invalidateQueries({ queryKey: devicesKey });
  };
  const failed = (e: unknown) => {
    setConfirming(null);
    setMessage(friendlyError(e));
  };

  const revoke = useMutation({
    mutationFn: (session: SessionDto) => revokeBrowser(session.id),
    onSuccess: (_data, session) => done(session.isCurrent),
    onError: failed,
  });
  const revokeAllBrowsers = useMutation({ mutationFn: revokeWebSessions, onSuccess: () => done(isWeb()), onError: failed });
  const revokeAllIphones = useMutation({ mutationFn: signOutAllIphones, onSuccess: () => done(false), onError: failed });

  const browsers = devices.data?.browsers ?? [];
  const iphones = devices.data?.iphones ?? [];

  return (
    <Screen title="Devices and browsers" subtitle="Where you are signed in.">
      <BackButton />
      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}

      {confirming?.kind === 'browser' ? (
        <Card>
          <Text variant="heading">Sign out {confirming.session.deviceName}?</Text>
          <Text tone="muted">
            {confirming.session.isCurrent
              ? "This is the browser you're using right now. You'll be signed out and asked to sign in again."
              : 'It will be signed out right away.'}
          </Text>
          <Button
            label={confirming.session.isCurrent ? 'Sign out this browser' : 'Sign out'}
            variant="danger"
            busy={revoke.isPending}
            onPress={() => revoke.mutate(confirming.session)}
          />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(null)} />
        </Card>
      ) : null}

      {confirming?.kind === 'all-browsers' ? (
        <Card>
          <Text variant="heading">Sign out of all browsers?</Text>
          <Text tone="muted">Every browser you are signed in on, including this one, will be signed out right away. Your iPhones stay signed in.</Text>
          <Button label="Sign out all browsers" variant="danger" busy={revokeAllBrowsers.isPending} onPress={() => revokeAllBrowsers.mutate()} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(null)} />
        </Card>
      ) : null}

      {confirming?.kind === 'all-iphones' ? (
        <Card>
          <Text variant="heading">Sign out all iPhones?</Text>
          <Text tone="muted">Every iPhone you use will be signed out. Each one signs in again when you next open the app.</Text>
          <Button label="Sign out all iPhones" variant="danger" busy={revokeAllIphones.isPending} onPress={() => revokeAllIphones.mutate()} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(null)} />
        </Card>
      ) : null}

      {devices.isLoading ? <Text tone="muted">Loading...</Text> : null}
      {devices.isError ? <Text tone="danger">{friendlyError(devices.error)}</Text> : null}
      {devices.data?.failed.map((half) => (
        <Text key={half} tone="muted" accessibilityRole="alert">
          {`Couldn't load your ${half}. Try again in a moment.`}
        </Text>
      ))}

      <Card>
        <Text variant="heading">Browsers</Text>
        {devices.data && browsers.length === 0 && !devices.data.failed.includes('browsers') ? <Text tone="muted">No browsers yet.</Text> : null}
        {browsers.map((s) => (
          <ListRow
            key={s.id}
            title={s.isCurrent ? `${s.deviceName} (this browser)` : s.deviceName}
            subtitle={formatLastSeen(s.lastSeenAt)}
            trailing={
              <Button
                label="Sign out"
                accessibilityLabel={`Sign out ${s.deviceName}`}
                variant="secondary"
                onPress={() => {
                  setMessage(null);
                  setConfirming({ kind: 'browser', session: s });
                }}
              />
            }
          />
        ))}
        <Button
          label="Sign out of all browsers"
          variant="secondary"
          onPress={() => {
            setMessage(null);
            setConfirming({ kind: 'all-browsers' });
          }}
        />
      </Card>

      <Card>
        <Text variant="heading">iPhones</Text>
        {devices.data && iphones.length === 0 && !devices.data.failed.includes('iPhones') ? (
          <Text tone="muted">No iPhones yet. Install the app from TestFlight and sign in.</Text>
        ) : null}
        {iphones.map((p) => (
          <ListRow key={p.id} title={p.deviceName} subtitle={formatLastSeen(p.lastSeenAt)} />
        ))}
        {iphones.length > 0 ? (
          <Button
            label="Sign out all iPhones"
            variant="secondary"
            onPress={() => {
              setMessage(null);
              setConfirming({ kind: 'all-iphones' });
            }}
          />
        ) : null}
      </Card>
    </Screen>
  );
}
