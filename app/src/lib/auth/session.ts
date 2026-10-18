// Auth state for the HTTP client: which platform we are, the in-memory access token, and the shared refresh.
// Web authenticates with the same-origin cookie. iOS sends a bearer access token and refreshes it with the refresh token.
import { Platform } from 'react-native';
import { logger } from './log';
import { MemoryTokenStore, type TokenStore } from './tokenStore';

export type AuthPlatform = 'web' | 'native';

export type AuthConfig = {
  platform: AuthPlatform;
  tokenStore: TokenStore;
  /** Called when the session cannot continue: native goes to pairing, web goes to sign-in. */
  onAuthLost: (destination: 'pair' | 'login') => void;
  /** Web only: runs a fresh passkey prompt. Rejects if the person cancels. */
  reauth: () => Promise<void>;
};

const defaults = (): AuthConfig => ({
  platform: Platform.OS === 'web' ? 'web' : 'native',
  tokenStore: new MemoryTokenStore(),
  onAuthLost: () => {},
  reauth: () => Promise.reject(new Error('Re-authentication is not set up')),
});

let config: AuthConfig = defaults();
let accessToken: string | null = null;
let serverUrl: string | null = null;
let refreshing: Promise<boolean> | null = null;
let reauthing: Promise<void> | null = null;
/** After a 429 on refresh: no new attempt before this time (ms since the epoch). Never cleared by a failure, only by waiting or a reset. */
let refreshNotBefore = 0;

export function configureAuth(partial: Partial<AuthConfig>) {
  config = { ...config, ...partial };
}

/** Back to defaults (tests). */
export function resetAuth() {
  config = defaults();
  accessToken = null;
  serverUrl = null;
  refreshing = null;
  reauthing = null;
  refreshNotBefore = 0;
}

export const isWeb = () => config.platform === 'web';
export const getTokenStore = () => config.tokenStore;
export const getAccessToken = () => accessToken;

/** The web app is normally served by the API itself, so with no address configured it talks to the origin it came from (which `connect-src 'self'` allows). */
const pageOrigin = (): string | undefined => {
  const origin = isWeb() ? (globalThis as { location?: { origin?: string } }).location?.origin : undefined;
  return origin && origin !== 'null' ? origin : undefined;
};

export const baseUrl = (): string => serverUrl ?? process.env.EXPO_PUBLIC_API_URL ?? pageOrigin() ?? 'http://localhost:5480';

/** After pairing: remember the server, keep the access token in memory and the refresh token in the store. */
export async function startSession(tokens: { accessToken: string; refreshToken: string; serverUrl: string }) {
  accessToken = tokens.accessToken;
  serverUrl = tokens.serverUrl;
  await config.tokenStore.save({ refreshToken: tokens.refreshToken, serverUrl: tokens.serverUrl });
}

/** On app start (iOS): true if this device was paired before. */
export async function restoreSession(): Promise<boolean> {
  const stored = await config.tokenStore.load();
  if (!stored) return false;
  serverUrl = stored.serverUrl;
  return true;
}

/** Forget this device's credentials but keep local data. */
export async function endSession() {
  accessToken = null;
  await config.tokenStore.clear();
}

/** Exchanges the refresh token for new tokens. Concurrent callers share one request. */
export function refreshAccessToken(): Promise<boolean> {
  // The server asked us to slow down: keep the credentials and try again once it said it is fine.
  if (!refreshing && Date.now() < refreshNotBefore) return Promise.resolve(false);
  refreshing ??= doRefresh().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

async function doRefresh(): Promise<boolean> {
  const stored = await config.tokenStore.load();
  if (!stored) {
    accessToken = null;
    config.onAuthLost('pair');
    return false;
  }
  try {
    const response = await fetch(`${stored.serverUrl}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
      body: new URLSearchParams({ grant_type: 'refresh_token', refresh_token: stored.refreshToken }).toString(),
    });
    const body = response.ok ? ((await response.json()) as { access_token?: string; refresh_token?: string }) : null;
    if (body?.access_token && body.refresh_token) {
      accessToken = body.access_token;
      serverUrl = stored.serverUrl;
      await config.tokenStore.save({ refreshToken: body.refresh_token, serverUrl: stored.serverUrl });
      return true;
    }
    // Only a refused token means this device was revoked: 400 invalid_grant or 401. Anything else (a 5xx, a 429, another 4xx we don't
    // know) is the server or the network having a bad moment: keep the credentials and let the caller fail; a later call tries again.
    if (response.status === 429) {
      refreshNotBefore = Date.now() + retryAfterMs(response.headers.get('Retry-After'));
      return false;
    }
    if (!(await isRevoked(response))) return false;
  } catch {
    logger.warn('Could not reach the server to refresh the session.');
    return false;
  }
  await endSession();
  config.onAuthLost('pair');
  return false;
}

const DEFAULT_BACKOFF_MS = 60_000;
const MAX_BACKOFF_MS = 10 * 60_000;

/** `Retry-After` is a number of seconds or a date; absent or unreadable means a minute. Never longer than ten minutes. */
function retryAfterMs(header: string | null): number {
  let ms = DEFAULT_BACKOFF_MS;
  if (header) {
    const seconds = Number(header);
    if (Number.isFinite(seconds)) ms = seconds * 1000;
    else if (!Number.isNaN(Date.parse(header))) ms = Date.parse(header) - Date.now();
  }
  return Math.min(Math.max(ms, 1000), MAX_BACKOFF_MS);
}

/** A 401, or a 400 whose OAuth error is `invalid_grant` (the refresh token is spent, expired or revoked). */
async function isRevoked(response: Response): Promise<boolean> {
  if (response.status === 401) return true;
  if (response.status !== 400) return false;
  try {
    return ((await response.json()) as { error?: string }).error === 'invalid_grant';
  } catch {
    return false;
  }
}

/** Web: ask for a fresh passkey assertion. Concurrent callers share one prompt. */
export function reauthenticate(): Promise<void> {
  reauthing ??= config.reauth().finally(() => {
    reauthing = null;
  });
  return reauthing;
}

export function notifyAuthLost(destination: 'pair' | 'login') {
  config.onAuthLost(destination);
}

/** Headers for one request, per platform. May refresh first when this device has a refresh token but no access token yet. */
export async function authHeaders(method: string): Promise<Record<string, string>> {
  if (isWeb()) {
    return method === 'GET' || method === 'HEAD' ? {} : { 'X-CSRF': '1' };
  }
  if (!accessToken && (await config.tokenStore.load())) await refreshAccessToken();
  return accessToken ? { Authorization: `Bearer ${accessToken}` } : {};
}
