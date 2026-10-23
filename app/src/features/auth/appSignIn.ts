// After the iPhone signs in: tell the server which install this is, so it shows under "Devices and browsers" (docs/14).
import { ApiError, customFetch } from '@/lib/http';
import { logger } from '@/lib/auth/log';
import { getInstallId } from '@/lib/auth/installId';
import type { AppSignInDto } from './contract';

export const DEVICE_NAME = 'iPhone';

/**
 * Registers this install. A 404 means the server does not have the route yet: ignored. A 403 account.not_linked is handled by the
 * HTTP client (it opens the not-linked page). Anything else is logged and skipped, because the sign-in itself worked.
 */
export async function registerThisInstall(): Promise<void> {
  try {
    await customFetch<{ data: AppSignInDto }>('/api/v1/me/app-sign-ins', {
      method: 'POST',
      body: JSON.stringify({ deviceName: DEVICE_NAME }),
      headers: { 'X-Install-Id': await getInstallId() },
    });
  } catch (e) {
    if (e instanceof ApiError && (e.status === 404 || e.status === 403)) return;
    logger.warn('Could not register this iPhone with the server.');
  }
}
