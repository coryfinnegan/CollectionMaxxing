// Me > Devices and browsers: where you are signed in, revoke one, and add an iPhone with a QR code and a 6-digit code.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { View } from 'react-native';
import { friendlyError } from '@/lib/problem';
import { type PairDeviceDto, type SessionDto, listSessions, pairDevice, revokeSession, revokeWebSessions } from './api';
import { getQrRenderer } from './services';
import { Button, Card, ListRow, Screen, Text } from '@/ui';
import { spacing } from '@/theme/tokens';

const sessionsKey = ['me', 'sessions'] as const;

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

/** Seconds left until `expiresAt`, ticking once a second. */
export function useSecondsLeft(expiresAt: string | null): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return expiresAt ? Math.max(0, Math.ceil((new Date(expiresAt).getTime() - now) / 1000)) : 0;
}

const clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;

function PairingCard({ pairing, onClose }: { pairing: PairDeviceDto; onClose: () => void }) {
  const renderQr = getQrRenderer();
  const seconds = useSecondsLeft(pairing.expiresAt);
  return (
    <Card>
      <Text variant="heading">Add a device</Text>
      {seconds > 0 ? (
        <>
          <Text tone="muted">On your iPhone, open CollectionMaxxing and scan this QR code, or enter the code below.</Text>
          <View style={{ alignItems: 'center', gap: spacing.md }}>
            {renderQr({ value: JSON.stringify({ serverUrl: pairing.serverUrl, token: pairing.token }), size: 200 })}
            <Text variant="title" accessibilityLabel={`Code ${pairing.code.split('').join(' ')}`}>
              {pairing.code}
            </Text>
            <Text tone="muted">Expires in {clock(seconds)}</Text>
          </View>
        </>
      ) : (
        <Text accessibilityRole="alert">This code has expired. Add a device again to get a new one.</Text>
      )}
      <Button label="Done" variant="secondary" onPress={onClose} />
    </Card>
  );
}

export function DevicesScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const sessions = useQuery({ queryKey: sessionsKey, queryFn: listSessions });
  const [confirming, setConfirming] = useState<SessionDto | null>(null);
  const [confirmingAll, setConfirmingAll] = useState(false);
  const [pairing, setPairing] = useState<PairDeviceDto | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const revoke = useMutation({
    mutationFn: (session: SessionDto) => revokeSession(session),
    onSuccess: (_data, session) => {
      setConfirming(null);
      if (session.isCurrent) router.replace('/login');
      else queryClient.invalidateQueries({ queryKey: sessionsKey });
    },
    onError: (e) => setMessage(friendlyError(e)),
  });

  const revokeAll = useMutation({
    mutationFn: revokeWebSessions,
    onSuccess: () => {
      setConfirmingAll(false);
      router.replace('/login');
    },
    onError: (e) => setMessage(friendlyError(e)),
  });

  const add = useMutation({
    mutationFn: pairDevice,
    onSuccess: setPairing,
    onError: (e) => setMessage(friendlyError(e)),
  });

  return (
    <Screen title="Devices and browsers" subtitle="Where you are signed in.">
      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}

      {pairing ? <PairingCard pairing={pairing} onClose={() => setPairing(null)} /> : null}

      {confirming ? (
        <Card>
          <Text variant="heading">Sign out {confirming.deviceName}?</Text>
          <Text tone="muted">
            {confirming.isCurrent
              ? "This is the device you're using right now. You'll be signed out and asked to sign in again."
              : 'It will be signed out right away.'}
          </Text>
          <Button
            label={confirming.isCurrent ? 'Sign out this device' : 'Sign out'}
            variant="danger"
            busy={revoke.isPending}
            onPress={() => revoke.mutate(confirming)}
          />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(null)} />
        </Card>
      ) : null}

      {confirmingAll ? (
        <Card>
          <Text variant="heading">Sign out of all browsers?</Text>
          <Text tone="muted">
            Every browser you are signed in on, including this one, will be signed out right away. Your phones stay signed in.
          </Text>
          <Button label="Sign out all" variant="danger" busy={revokeAll.isPending} onPress={() => revokeAll.mutate()} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirmingAll(false)} />
        </Card>
      ) : null}

      <Card>
        {sessions.isLoading ? <Text tone="muted">Loading...</Text> : null}
        {sessions.isError ? <Text tone="danger">{friendlyError(sessions.error)}</Text> : null}
        {(sessions.data ?? []).map((s) => (
          <ListRow
            key={s.id}
            title={s.isCurrent ? `${s.deviceName} (this device)` : s.deviceName}
            subtitle={formatLastSeen(s.lastSeenAt)}
            trailing={
              <Button
                label="Revoke"
                accessibilityLabel={`Revoke ${s.deviceName}`}
                variant="secondary"
                onPress={() => {
                  setMessage(null);
                  setConfirming(s);
                }}
              />
            }
          />
        ))}
      </Card>

      <Button
        label="Sign out of all browsers"
        variant="secondary"
        onPress={() => {
          setMessage(null);
          setConfirmingAll(true);
        }}
      />

      <Button label="Add a device" busy={add.isPending} onPress={() => {
        setMessage(null);
        add.mutate();
      }} />
    </Screen>
  );
}
