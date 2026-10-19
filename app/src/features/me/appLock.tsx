// Face ID app lock (docs/07): when on, the app asks for Face ID (or the passcode) at launch and when it comes back from the
// background after a short grace period. While locked the content stays hidden.
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { AppState, type AppStateStatus, View } from 'react-native';
import { create } from 'zustand';
import { isWeb } from '@/lib/auth/session';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import { getBiometrics, getSettingsStorage } from './services';

export const LOCK_GRACE_MS = 5 * 60_000;
const KEY = 'face-id-lock';

/** On unless the person turned it off (docs/07). */
const read = () => getSettingsStorage().getString(KEY) !== 'off';

type LockSetting = { enabled: boolean; set(enabled: boolean): void; reload(): void };

export const useLockSetting = create<LockSetting>((set) => ({
  enabled: true,
  set: (enabled) => {
    getSettingsStorage().set(KEY, enabled ? 'on' : 'off');
    set({ enabled });
  },
  reload: () => set({ enabled: read() }),
}));

type AppStateSource = { addEventListener(type: 'change', listener: (state: AppStateStatus) => void): { remove(): void } };

export function AppLockGate({
  children,
  appState = AppState,
  now = Date.now,
}: {
  children: ReactNode;
  appState?: AppStateSource;
  now?: () => number;
}) {
  const native = !isWeb();
  const enabled = useLockSetting((s) => s.enabled);
  const reload = useLockSetting((s) => s.reload);
  const [locked, setLocked] = useState(false);
  const [failed, setFailed] = useState(false);
  const [covered, setCovered] = useState(false);
  const colors = useColors();
  const leftAt = useRef<number | null>(null);

  const unlock = useCallback(async () => {
    const ok = await getBiometrics().authenticate('Unlock CollectionMaxxing');
    setFailed(!ok);
    if (ok) setLocked(false);
  }, []);

  const lock = useCallback(async () => {
    if (!(await getBiometrics().isAvailable())) return; // nothing to ask with: don't lock the person out
    setLocked(true);
    void unlock();
  }, [unlock]);

  useEffect(() => {
    if (!native) return;
    reload();
    if (read()) queueMicrotask(() => void lock());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [native]);

  useEffect(() => {
    if (!native) return;
    const sub = appState.addEventListener('change', (state) => {
      // The app switcher snapshots the screen as the app goes inactive: cover it first (works with the lock off too).
      setCovered(state !== 'active');
      if (state === 'background') leftAt.current = now();
      if (state === 'active' && leftAt.current !== null) {
        const away = now() - leftAt.current;
        leftAt.current = null;
        if (enabled && away >= LOCK_GRACE_MS) void lock();
      }
    });
    return () => sub.remove();
  }, [native, enabled, appState, now, lock]);

  if (!native) return <>{children}</>;
  // One stable tree, so covering and locking never remount the content.
  return (
    <View style={{ flex: 1 }}>
      <View style={locked || covered ? { display: 'none' } : { flex: 1 }} pointerEvents={locked || covered ? 'none' : 'auto'}>
        {children}
      </View>
      {locked ? (
        <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', gap: spacing.md, padding: spacing.xl }}>
          <Text variant="heading">CollectionMaxxing is locked</Text>
          {failed ? (
            <Text tone="danger" accessibilityRole="alert">
              Couldn&apos;t unlock. Try Face ID or your passcode again.
            </Text>
          ) : null}
          <Button label="Unlock" onPress={() => void unlock()} />
        </View>
      ) : null}
      {covered ? (
        <View
          testID="snapshot-cover"
          accessibilityElementsHidden
          importantForAccessibility="no-hide-descendants"
          pointerEvents="none"
          style={{
            position: 'absolute',
            top: 0,
            right: 0,
            bottom: 0,
            left: 0,
            alignItems: 'center',
            justifyContent: 'center',
            backgroundColor: colors.ground,
          }}
        >
          <Text variant="heading">CollectionMaxxing</Text>
        </View>
      ) : null}
    </View>
  );
}
