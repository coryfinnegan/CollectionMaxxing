// Settings: Recognition and Security. The iPhone shows both; the web shows Sign out only.
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { logout } from '@/features/auth/api';
import { clearOwnedData } from '@/features/offline/ownedCache';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Screen, Text } from '@/ui';
import { useLockSetting } from './appLock';
import { getBiometrics, getVision } from './services';
import { unpairThisDevice } from './unpair';

export function RecognitionSection() {
  const [available, setAvailable] = useState<boolean | null>(null);
  useEffect(() => {
    let live = true;
    getVision()
      .isAvailable()
      .then((ok) => live && setAvailable(ok))
      .catch(() => live && setAvailable(false));
    return () => {
      live = false;
    };
  }, []);
  return (
    <Card>
      <Text variant="heading">Recognition</Text>
      {available === null ? (
        <Text tone="muted">Checking…</Text>
      ) : available ? (
        <>
          <Text>Engine: Apple on-device model</Text>
          <Text tone="muted">Apple Intelligence is on. Covers are read on this iPhone and never leave it.</Text>
        </>
      ) : (
        <>
          <Text>Engine: Vision text fallback</Text>
          <Text tone="muted">Apple Intelligence isn&apos;t available, so scans read the text on the cover instead. Turn it on in iOS Settings for better matches.</Text>
        </>
      )}
    </Card>
  );
}

function FaceIdRow() {
  const enabled = useLockSetting((s) => s.enabled);
  const setEnabled = useLockSetting((s) => s.set);
  const reload = useLockSetting((s) => s.reload);
  const [canLock, setCanLock] = useState(true);
  useEffect(() => {
    reload();
    void getBiometrics().isAvailable().then(setCanLock);
  }, [reload]);
  return (
    <>
      <Text>{enabled ? 'Face ID lock is on. CollectionMaxxing asks for Face ID when you open it or come back after a short while.' : 'Face ID lock is off.'}</Text>
      {!canLock ? <Text tone="muted">Set up Face ID or a passcode in iOS Settings to use the lock.</Text> : null}
      <Button
        label={enabled ? 'Turn off Face ID lock' : 'Turn on Face ID lock'}
        variant="secondary"
        onPress={() => setEnabled(!enabled)}
      />
    </>
  );
}

export function SecuritySection() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const signOut = useMutation({
    mutationFn: logout,
    onSuccess: () => {
      clearOwnedData(); // the offline "do I own this?" copy must not outlive the sign-in
      queryClient.clear();
      router.replace('/login');
    },
    onError: (e) => setMessage(friendlyError(e)),
  });

  const unpair = useMutation({
    mutationFn: () => unpairThisDevice(queryClient),
    onSuccess: () => router.replace('/pair'),
    onError: (e) => setMessage(friendlyError(e)),
  });

  if (isWeb()) {
    return (
      <Card>
        <Text variant="heading">Security</Text>
        {message ? <Text tone="danger" accessibilityRole="alert">{message}</Text> : null}
        <Button label="Sign out" variant="secondary" busy={signOut.isPending} onPress={() => signOut.mutate()} />
      </Card>
    );
  }
  return (
    <Card>
      <Text variant="heading">Security</Text>
      <FaceIdRow />
      {message ? <Text tone="danger" accessibilityRole="alert">{message}</Text> : null}
      {confirming ? (
        <>
          <Text>This signs this iPhone out of your library and removes its saved sign-in, offline items and scan tray. Your library on the server isn&apos;t touched. You&apos;ll pair again to use it.</Text>
          <Button label="Unpair and remove local data" variant="danger" busy={unpair.isPending} onPress={() => unpair.mutate()} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(false)} />
        </>
      ) : (
        <Button label="Unpair this iPhone" variant="secondary" onPress={() => setConfirming(true)} />
      )}
    </Card>
  );
}

export function SettingsScreen() {
  return (
    <Screen title="Settings" subtitle={isWeb() ? 'Preferences for this browser.' : 'Recognition and security on this iPhone.'}>
      {isWeb() ? null : <RecognitionSection />}
      <SecuritySection />
    </Screen>
  );
}
