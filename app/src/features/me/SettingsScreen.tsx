// Settings: Recognition, Appearance and Security. The iPhone shows all three; the web shows Sign out only.
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { logout } from '@/features/auth/api';
import { navigateTo } from '@/features/auth/navigation';
import { LibrarySwitcher } from '@/features/onboarding/LibrarySwitcher';
import { LeaveLibraryCard } from '@/features/onboarding/LeaveLibraryCard';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, ListRow, LoadingState, Screen, Text } from '@/ui';
import type { RecognitionState } from '@/features/scan/recognition/EngineSelector';
import { EvalsRow } from '@/features/evals/EvalsRow';
import { AppearanceSection } from './AppearanceSection';
import { GetIphoneAppRow } from './GetIphoneAppRow';
import { useLockSetting } from './appLock';
import { clearLocalData } from './localData';
import { getBiometrics, getVision } from './services';
import { signOutThisDevice } from './unpair';

const APPLE_INTELLIGENCE_LABEL: Record<RecognitionState['appleIntelligence'], string> = {
  on: 'On',
  appleIntelligenceOff: 'Off',
  modelNotReady: 'Still getting ready',
  osTooOld: 'Needs a newer iOS',
  notSupported: 'Not on this iPhone',
  unknown: 'Checking…',
};

export function RecognitionSection() {
  const [state, setState] = useState<RecognitionState | null>(null);
  useEffect(() => {
    let live = true;
    getVision()
      .state()
      .then((s) => live && setState(s))
      .catch(() => live && setState({ engine: 'vision_pipeline', appleIntelligence: 'unknown' }));
    return () => {
      live = false;
    };
  }, []);
  return (
    <Card>
      <Text variant="heading">Recognition</Text>
      {state === null ? (
        <LoadingState label="Checking…" />
      ) : state.engine === 'apple_model' ? (
        <>
          <Text>Engine: Apple on-device model</Text>
          <Text>{`Apple Intelligence: ${APPLE_INTELLIGENCE_LABEL[state.appleIntelligence]}`}</Text>
          <Text tone="muted">Apple Intelligence is on. Covers are read on this iPhone and never leave it.</Text>
        </>
      ) : (
        <>
          <Text>Engine: Vision text fallback</Text>
          <Text>{`Apple Intelligence: ${APPLE_INTELLIGENCE_LABEL[state.appleIntelligence]}`}</Text>
          <Text tone="muted">Apple Intelligence isn&apos;t available, so scans read the text on the cover instead. Turn it on in iOS Settings for better matches.</Text>
        </>
      )}
      <EvalsRow />
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
    // Every registered clearer runs (the offline "do I own this?" copy must not outlive the sign-in), then the query cache.
    mutationFn: async () => {
      const { logoutUrl } = await logout();
      await clearLocalData(queryClient);
      return logoutUrl;
    },
    // Leave for Auth0's sign-out, which brings the browser back to the sign-in page.
    onSuccess: (logoutUrl) => navigateTo(logoutUrl),
    onError: (e) => setMessage(friendlyError(e)),
  });

  const signOutDevice = useMutation({
    mutationFn: () => signOutThisDevice(queryClient),
    onSuccess: () => router.replace('/login'),
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
          <Text>This signs this iPhone out of your library and removes its saved sign-in, offline items and scan tray. Your library on the server isn&apos;t touched. You&apos;ll sign in again to use it.</Text>
          <Button label="Sign out and remove local data" variant="danger" busy={signOutDevice.isPending} onPress={() => signOutDevice.mutate()} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(false)} />
        </>
      ) : (
        <Button label="Sign out of this iPhone" variant="secondary" onPress={() => setConfirming(true)} />
      )}
    </Card>
  );
}

/** iPhone: the library list (with several), New library and Join a library. Web has these in the sidebar. */
function LibrariesSection() {
  return (
    <Card>
      <Text variant="heading">Libraries</Text>
      <LibrarySwitcher />
    </Card>
  );
}

export function SettingsScreen() {
  const router = useRouter();
  return (
    <Screen title="Settings" subtitle={isWeb() ? 'Preferences for this browser.' : 'Recognition, appearance and security on this iPhone.'}>
      {isWeb() ? null : <RecognitionSection />}
      {isWeb() ? null : <AppearanceSection />}
      {isWeb() ? <Card><GetIphoneAppRow /></Card> : null}
      <Card><ListRow title="Devices and browsers" subtitle="Your browsers and iPhones" onPress={() => router.push('/me/devices')} trailing={<Text tone="muted">›</Text>} /></Card>
      {isWeb() ? <Card><ListRow title="Sign-in methods" subtitle="Google and Apple accounts" onPress={() => router.push('/me/sign-in-methods')} trailing={<Text tone="muted">›</Text>} /></Card> : null}
      {isWeb() ? null : <LibrariesSection />}
      <LeaveLibraryCard />
      <SecuritySection />
      <Card><ListRow title="About and privacy" onPress={() => router.push('/about')} trailing={<Text tone="muted">›</Text>} /></Card>
    </Screen>
  );
}
