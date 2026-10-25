// After the iPhone signs in: tell the server which install this is, so it shows under "Devices and browsers" (docs/14).
import { AppState } from 'react-native';
import { ApiError, customFetch } from '@/lib/http';
import { logger } from '@/lib/auth/log';
import { getInstallId } from '@/lib/auth/installId';
import { getNativeAuth, isWeb } from '@/lib/auth/session';
import type { AppSignInDto } from './contract';

export const DEVICE_NAME = 'iPhone';

/** A foreground return re-registers only when the last registration is older than this. The server upserts by X-Install-Id, so repeating is safe. */
const REFRESH_AFTER_MS = 24 * 60 * 60 * 1000;

/** When the server last confirmed this install (0 = never). Only a confirmed registration moves the clock, so a failure retries on the next foreground return. */
let lastRegisteredAt = 0;

/**
 * One registration POST. True when the server confirmed it; a 404 counts as confirmed (the server predates the route,
 * so there is nothing to retry). Every other failure is logged and skipped, because the sign-in itself worked.
 */
async function postInstallRegistration(): Promise<boolean> {
  try {
    await customFetch<{ data: AppSignInDto }>('/api/v1/me/app-sign-ins', {
      method: 'POST',
      body: JSON.stringify({ deviceName: DEVICE_NAME }),
      headers: { 'X-Install-Id': await getInstallId() },
    });
    return true;
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return true;
    logger.warn('Could not register this iPhone with the server.');
    return false;
  }
}

/** Registers this install (sign-in, start-up). Never throws: failures are logged and skipped, because the sign-in itself worked. */
export async function registerThisInstall(): Promise<void> {
  if (await postInstallRegistration()) lastRegisteredAt = Date.now();
}

/** Registers again only when the last registration is more than a day old. Never throws. */
export async function refreshInstallRegistration(now: number = Date.now()): Promise<void> {
  if (now - lastRegisteredAt <= REFRESH_AFTER_MS) return;
  if (await postInstallRegistration()) lastRegisteredAt = now;
}

/** Back to never-registered (tests). */
export function resetInstallRegistration() {
  lastRegisteredAt = 0;
}

/**
 * Re-registers on foreground returns (installed once from the root layout, iPhone only). A signed-out phone skips:
 * only a working sign-in registers. Returns the unsubscribe function.
 */
export function installForegroundRegistration(): () => void {
  if (isWeb()) return () => {};
  const subscription = AppState.addEventListener('change', (state) => {
    if (state !== 'active') return;
    getNativeAuth()
      .hasValidCredentials()
      .then((valid) => {
        if (valid) void refreshInstallRegistration();
      })
      .catch(() => {});
  });
  return () => subscription.remove();
}
