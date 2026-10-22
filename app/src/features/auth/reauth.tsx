// "Confirm it's you": explains why, then sends the browser through a fresh sign-in. Opened by the HTTP client when the server
// answers 401 reauth_required (a sensitive change needs a sign-in from the last few minutes).
import { useSyncExternalStore } from 'react';
import { Modal, View } from 'react-native';
import { ReauthCancelledError } from '@/lib/auth/errors';
import { radius, spacing } from '@/theme/tokens';
import { Button, Card, Text } from '@/ui';
import { currentLocalUrl, loginUrl, navigateTo } from './navigation';

type Pending = { reject: (e: Error) => void };

let pending: Pending | null = null;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

/**
 * Opens the explanation. The returned promise never resolves: confirming leaves the page for the sign-in, which brings the
 * person back to where they were. Cancelling rejects, so the request that needed it fails with a plain message.
 */
export function requestReauth(): Promise<void> {
  return new Promise<void>((_resolve, reject) => {
    pending = { reject };
    emit();
  });
}

function confirm() {
  pending = null;
  emit();
  navigateTo(loginUrl({ reauth: true, returnUrl: currentLocalUrl() }));
}

function cancel() {
  const current = pending;
  pending = null;
  emit();
  current?.reject(new ReauthCancelledError());
}

/** Mounted once, under the signed-in pages. */
export function ReauthPrompt() {
  const open = useSyncExternalStore(subscribe, () => pending !== null);
  return (
    <Modal visible={open} transparent animationType="fade" onRequestClose={cancel}>
      <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center', padding: spacing.lg, backgroundColor: 'rgba(0,0,0,0.4)' }}>
        <View style={{ width: '100%', maxWidth: 420, borderRadius: radius.md }}>
          <Card>
            <Text variant="heading">Confirm it&apos;s you</Text>
            <Text>This change needs a fresh sign-in. You&apos;ll sign in again, then come straight back here to finish.</Text>
            <Button label="Sign in again" onPress={confirm} />
            <Button label="Not now" variant="secondary" onPress={cancel} />
          </Card>
        </View>
      </View>
    </Modal>
  );
}
