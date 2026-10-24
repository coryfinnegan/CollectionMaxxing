// Auth state for the HTTP client: which platform we are and how a request gets its credentials.
// Web authenticates with the same-origin cookie. iOS sends a bearer token from Auth0's Credentials Manager (see ./native).
import { Platform } from 'react-native';
import { createAuth0Native } from './auth0Native';
import { AuthLostError, type NativeAuth } from './native';

export type AuthPlatform = 'web' | 'native';
/** Where the person goes when the app cannot continue: Sign in. */
export type AuthLostDestination = 'login';

export type AuthConfig = {
  platform: AuthPlatform;
  native: NativeAuth;
  onAuthLost: (destination: AuthLostDestination) => void;
  /** Web only: sends the browser through a fresh sign-in. Rejects if the person cancels. */
  reauth: () => Promise<void>;
};

// The native sign-in exists from the first render (not installed by an effect later), so start-up routing can ask it right away.
const defaults = (): AuthConfig => ({
  platform: Platform.OS === 'web' ? 'web' : 'native',
  native: createAuth0Native(),
  onAuthLost: () => {},
  reauth: () => Promise.reject(new Error('Re-authentication is not set up')),
});

let config: AuthConfig = defaults();
let refreshing: Promise<string | null> | null = null;
let reauthing: Promise<void> | null = null;

export function configureAuth(partial: Partial<AuthConfig>) {
  config = { ...config, ...partial };
}

/** Back to defaults (tests). */
export function resetAuth() {
  config = defaults();
  refreshing = null;
  reauthing = null;
}

export const isWeb = () => config.platform === 'web';
export const getNativeAuth = () => config.native;

/** The web app is normally served by the API itself, so with no address configured it talks to the origin it came from (which `connect-src 'self'` allows). */
const pageOrigin = (): string | undefined => {
  const origin = isWeb() ? (globalThis as { location?: { origin?: string } }).location?.origin : undefined;
  return origin && origin !== 'null' ? origin : undefined;
};

export const baseUrl = (): string => process.env.EXPO_PUBLIC_API_URL ?? pageOrigin() ?? 'http://localhost:5480';

/** Drop this device's stored sign-in but keep local data. */
export async function endSession() {
  await config.native.clear();
}

/**
 * iOS: a bearer token for the next request, or null when there are definitely no credentials (the person is sent to Sign in).
 * Any other failure (network, timeout, 5xx, 429) is thrown as it is and never touches the credentials.
 */
async function nativeToken(forceRefresh: boolean): Promise<string | null> {
  const { native, onAuthLost } = config; // as configured when the request started, even if the app installs its own meanwhile
  try {
    return await native.getAccessToken(forceRefresh);
  } catch (e) {
    if (e instanceof AuthLostError) {
      onAuthLost('login');
      return null;
    }
    throw e;
  }
}

/** After a 401: one forced refresh, shared by every request that failed at the same time. Null means no new token (definitely lost, or the refresh could not run). */
export function refreshAccessToken(): Promise<string | null> {
  refreshing ??= nativeToken(true)
    .catch(() => null) // a transient failure: keep the credentials, let the caller surface the original error
    .finally(() => {
      refreshing = null;
    });
  return refreshing;
}

/** Web: ask the server for a fresh sign-in. Concurrent callers share one prompt. */
export function reauthenticate(): Promise<void> {
  reauthing ??= config.reauth().finally(() => {
    reauthing = null;
  });
  return reauthing;
}

export function notifyAuthLost(destination: AuthLostDestination) {
  config.onAuthLost(destination);
}

/** Headers for one request, per platform. */
export async function authHeaders(method: string): Promise<Record<string, string>> {
  if (isWeb()) {
    return method === 'GET' || method === 'HEAD' ? {} : { 'X-CSRF': '1' };
  }
  const token = await nativeToken(false);
  return token ? { Authorization: `Bearer ${token}` } : {};
}
