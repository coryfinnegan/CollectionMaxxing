// The iPhone's sign-in, behind an interface. The real one is Auth0's Credentials Manager (./auth0Native.native.ts); tests use
// MemoryNativeAuth. The one rule that matters: only a definite credentials failure may send the person to Sign in.

/** There are no usable credentials: none stored, or Auth0 refused the refresh token (invalid_grant, revoked). */
export class AuthLostError extends Error {
  constructor() {
    super('Signed out');
    this.name = 'AuthLostError';
  }
}

export interface NativeAuth {
  /** True if there is a sign-in that can still produce an access token (valid, or refreshable). */
  hasValidCredentials(): Promise<boolean>;
  /** Opens Auth0's sign-in and keeps the credentials. Rejects if the person backs out. */
  signIn(): Promise<void>;
  /**
   * The bearer token for our API; `forceRefresh` asks Auth0 for a new one. Rejects with AuthLostError when the credentials are
   * definitely gone, and with the original error for anything else (network, timeout, 5xx, 429), which must not sign anyone out.
   */
  getAccessToken(forceRefresh?: boolean): Promise<string>;
  /** Revokes the refresh token at Auth0, clears the credentials, ends the browser session. Local clearing happens even offline. */
  signOut(): Promise<{ revoked: boolean }>;
  /** Forgets the stored credentials without telling Auth0 (account deletion, local wipe). */
  clear(): Promise<void>;
}

const text = (value: unknown) => (typeof value === 'string' ? value : '');

/** Everything an SDK error might say about itself, lowercased. */
function describe(error: unknown): string {
  if (!error || typeof error !== 'object') return text(error).toLowerCase();
  const e = error as Record<string, unknown>;
  const json = e.json && typeof e.json === 'object' ? (e.json as Record<string, unknown>) : {};
  return [e.code, e.type, e.name, e.message, e.error, e.error_description, e.status, json.error, json.error_description]
    .map((v) => text(v) || (typeof v === 'number' ? String(v) : ''))
    .join(' ')
    .toLowerCase();
}

const TRANSIENT = /network|timeout|timed out|offline|internet|connection|unreachable|too_many|too many|rate.?limit|\b429\b|\b5\d\d\b|server_error|unavailable/;
const DEFINITE = /no_credentials|no credentials|no_refresh_token|no refresh token|revoked|login_required|invalid_refresh_token/;

/** True only when Auth0 or the Credentials Manager says the sign-in is definitely gone. Anything unclear is not. */
export function isDefiniteAuthFailure(error: unknown): boolean {
  const what = describe(error);
  if (what.includes('invalid_grant')) return true;
  if (TRANSIENT.test(what)) return false;
  return DEFINITE.test(what);
}

/** In-memory stand-in (tests, previews). */
export class MemoryNativeAuth implements NativeAuth {
  token: string | null;
  signedOut = false;

  constructor(token: string | null = 'mock-access') {
    this.token = token;
  }

  async hasValidCredentials() {
    return this.token !== null;
  }

  async signIn() {
    this.token = 'mock-access';
  }

  async getAccessToken() {
    if (this.token === null) throw new AuthLostError();
    return this.token;
  }

  async signOut() {
    this.signedOut = true;
    this.token = null;
    return { revoked: true };
  }

  async clear() {
    this.token = null;
  }
}

/** The default until the app installs the real thing (and on web, which never uses it). */
export class UnavailableNativeAuth implements NativeAuth {
  async hasValidCredentials() {
    return false;
  }

  async signIn(): Promise<void> {
    throw new Error('Sign-in is not available here');
  }

  async getAccessToken(): Promise<string> {
    throw new AuthLostError();
  }

  async signOut() {
    return { revoked: false };
  }

  async clear() {}
}
