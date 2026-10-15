// Unpair this iPhone (docs/07 "Sign out, devices and deletion"): revoke the refresh token, then wipe everything local.
import type { QueryClient } from '@tanstack/react-query';
import { logger } from '@/lib/auth/log';
import { getTokenStore } from '@/lib/auth/session';
import { clearLocalData } from './localData';

/** Tells the server to forget this device. Best effort: being offline must not trap the person in a paired app. */
async function revokeRefreshToken(): Promise<boolean> {
  const stored = await getTokenStore().load();
  if (!stored) return false;
  try {
    const response = await fetch(`${stored.serverUrl}/connect/revoke`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ token: stored.refreshToken, token_type_hint: 'refresh_token' }).toString(),
    });
    return response.ok;
  } catch {
    logger.warn('Could not reach the server to revoke this device.');
    return false;
  }
}

/** Returns whether the server confirmed the revoke; the local data is cleared either way. */
export async function unpairThisDevice(queryClient: QueryClient): Promise<{ revoked: boolean }> {
  const revoked = await revokeRefreshToken();
  await clearLocalData(queryClient);
  return { revoked };
}
