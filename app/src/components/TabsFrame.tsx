// The gates and bridges every tab-group page mounts: the app lock, the library
// gate, and the offline/scan stores. Shared by the native and web tab layouts
// (and the non-tab pages group) so a new bridge lands everywhere at once.
import type { ReactNode } from 'react';
import { ReauthPrompt } from '@/features/auth/reauth';
import { LibraryGate } from '@/features/onboarding/LibraryGate';
import { OwnedDataBridge } from '@/features/offline/OwnedDataBridge';
import { AppLockGate } from '@/features/me/appLock';
import { CardsBridge } from '@/features/scan/cards/CardsBridge';
import { FingerprintBridge } from '@/features/scan/fingerprints/FingerprintBridge';
import { SnapStoreBridge } from '@/features/scan/pipeline/SnapStoreBridge';

export function TabsFrame({ children }: { children: ReactNode }) {
  return (
    <AppLockGate>
      <LibraryGate />
      <OwnedDataBridge />
      <ReauthPrompt />
      <SnapStoreBridge />
      <FingerprintBridge />
      <CardsBridge />
      {children}
    </AppLockGate>
  );
}
