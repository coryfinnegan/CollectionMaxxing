// Sign out of this iPhone (docs/14 "Devices and revocation"): revoke the refresh token at Auth0, then wipe everything local.
import type { QueryClient } from '@tanstack/react-query';
import { getNativeAuth } from '@/lib/auth/session';
import { clearLocalData } from './localData';

/** Returns whether Auth0 confirmed the revoke; the credentials and local data are cleared either way, even offline. */
export async function signOutThisDevice(queryClient: QueryClient): Promise<{ revoked: boolean }> {
  const { revoked } = await getNativeAuth().signOut();
  await clearLocalData(queryClient);
  return { revoked };
}
