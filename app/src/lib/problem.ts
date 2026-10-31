// Turns an API failure (Problem Details) into friendly copy: what happened and what to do next.
import { ApiError } from './http';
import { logger } from './auth/log';
import { PasskeyCancelledError, PasskeyUnsupportedError, ReauthCancelledError } from './auth/errors';

const fieldCopy: Record<string, string> = {
  name: 'Give it a name.',
  title: 'Give it a title.',
};

// How each platform words a failed fetch: React Native ('Network request failed'), Chrome ('Failed to fetch'),
// Node ('fetch failed'), Firefox ('NetworkError ...'), plus timeouts.
const TRANSPORT_FAILURE = /network request failed|failed to fetch|fetch failed|networkerror|timeout|timed out/i;

function isTransportFailure(error: unknown): error is Error {
  return error instanceof Error && TRANSPORT_FAILURE.test(error.message);
}

export function friendlyError(error: unknown, fallback = "That didn't work. Try again."): string {
  if (error instanceof ReauthCancelledError) return 'Okay, nothing changed.';
  if (error instanceof PasskeyCancelledError) return "Passkey canceled. Try again when you're ready.";
  if (error instanceof PasskeyUnsupportedError) {
    return "This browser can't use passkeys. Open CollectionMaxxing in a current version of Edge, Chrome, Safari or Firefox.";
  }
  if (error instanceof ApiError) {
    const { status, problem } = error;
    if (status === 400 && problem.errors) {
      const field = Object.keys(problem.errors)[0];
      if (field) return fieldCopy[field.toLowerCase()] ?? problem.errors[field]?.[0] ?? fallback;
    }
    if (error.isReauthRequired) return "Sign in again to confirm it's you, then try again.";
    if (status === 401) return 'Your session ended. Sign in again.';
    if (status === 403) return "You don't have permission to change this library.";
    if (status === 404 && problem.code === 'provider_hit.not_found') return 'That search result has expired. Try adding it again.';
    if (status === 404) return "That's no longer here. Refresh and try again.";
    if (status === 412) return 'Someone changed this. Reload to see the latest.';
    if (status === 409) return problem.title ?? 'That conflicts with something that already exists.';
    if (status === 429) return 'Too many attempts. Wait a minute and try again.';
    return fallback;
  }
  // fetch itself failed: the server wasn't reachable. Transports word it differently per platform, so match
  // the message (consistent with TRANSIENT in auth/native.ts), not the constructor: a TypeError can also be a bug.
  if (isTransportFailure(error)) {
    return "Couldn't reach your server. Check your connection and try again.";
  }
  // Anything else is a bug, not a network failure: keep the copy generic and put the real error in the log.
  const detail = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
  logger.error(`Unexpected error (not a network failure): ${detail}`);
  return 'Something went wrong. Try again.';
}
