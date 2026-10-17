// The one fetch wrapper the generated client uses (see orval.config.ts).
// The base URL is the paired server on iOS, else EXPO_PUBLIC_API_URL; failures throw an ApiError that carries Problem Details.
// Auth (docs/07): web sends the cookie plus X-CSRF on writes; iOS sends a bearer token. Concurrent 401s share one refresh.
import {
  authHeaders,
  baseUrl,
  isWeb,
  notifyAuthLost,
  reauthenticate,
  refreshAccessToken,
} from './auth/session';

export type ProblemDetails = {
  type?: string | null;
  title?: string | null;
  detail?: string | null;
  status?: number | null;
  code?: string | null;
  errors?: Record<string, string[]>;
};

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;
  /** How long a 429 (or 503) asked the caller to wait, from `Retry-After`. Absent when the server did not say. */
  readonly retryAfterMs?: number;

  constructor(status: number, problem: ProblemDetails, retryAfterMs?: number) {
    super(problem.title ?? `Request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
    if (retryAfterMs !== undefined) this.retryAfterMs = retryAfterMs;
  }

  /** A 401 that asks for a fresh passkey rather than a new session. */
  get isReauthRequired(): boolean {
    return this.status === 401 && (this.problem.type?.endsWith('reauth_required') ?? this.problem.code === 'reauth_required');
  }
}

export const apiBaseUrl = (): string => baseUrl();

// Sign-in itself must never trigger a refresh or a redirect when it answers 401.
const isAuthEndpoint = (url: string) => /^\/(setup|account|connect)(\/|$|\?)/.test(url);

const parseBody = (text: string): unknown => {
  if (!text) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    return undefined; // e.g. an HTML error page
  }
};

/** `Retry-After` is a number of seconds or a date. */
function retryAfterMs(header: string | null): number | undefined {
  if (!header) return undefined;
  const seconds = Number(header);
  if (Number.isFinite(seconds)) return Math.max(0, seconds * 1000);
  const date = Date.parse(header);
  return Number.isNaN(date) ? undefined : Math.max(0, date - Date.now());
}

type Attempts = { refreshed: boolean; reauthed: boolean };

async function send<T>(url: string, options: RequestInit, attempts: Attempts): Promise<T> {
  const method = (options.method ?? 'GET').toUpperCase();
  const response = await fetch(`${apiBaseUrl()}${url}`, {
    ...options,
    ...(isWeb() ? { credentials: 'include' as const } : {}),
    headers: {
      Accept: 'application/json',
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...(await authHeaders(method)),
      ...options.headers,
    },
  });
  const body = parseBody(await response.text());

  if (response.ok) {
    return { data: body, status: response.status, headers: response.headers } as T;
  }

  const error = new ApiError(response.status, (body ?? {}) as ProblemDetails, retryAfterMs(response.headers.get('Retry-After')));
  if (response.status !== 401) throw error;

  // A recent-passkey check failed: ask for one passkey prompt, then retry once. Cancelling surfaces the prompt's error.
  if (error.isReauthRequired) {
    if (!isWeb() || attempts.reauthed) throw error;
    await reauthenticate();
    return send<T>(url, options, { ...attempts, reauthed: true });
  }

  if (isAuthEndpoint(url)) throw error;

  if (isWeb()) {
    notifyAuthLost('login');
    throw error;
  }
  if (!attempts.refreshed && (await refreshAccessToken())) {
    return send<T>(url, options, { ...attempts, refreshed: true });
  }
  throw error;
}

export const customFetch = async <T>(url: string, options: RequestInit = {}): Promise<T> =>
  send<T>(url, options, { refreshed: false, reauthed: false });
