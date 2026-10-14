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
}

export const isWeb = () => config.platform === 'web';
export const getTokenStore = () => config.tokenStore;
export const getAccessToken = () => accessToken;

export const baseUrl = (): string => serverUrl ?? process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5480';

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
    // A 5xx is the server having a bad moment, not a revoked device: keep the credentials and let the caller fail.
    if (response.status >= 500) return false;
  } catch {
    logger.warn('Could not reach the server to refresh the session.');
    return false;
  }
  await endSession();
  config.onAuthLost('pair');
  return false;
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
