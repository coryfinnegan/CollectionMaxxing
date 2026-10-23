// Turns an API failure (Problem Details) into friendly copy: what happened and what to do next.
import { ApiError } from './http';
import { PasskeyCancelledError, PasskeyUnsupportedError, ReauthCancelledError } from './auth/errors';
import { isWeb } from './auth/session';

const fieldCopy: Record<string, string> = {
  name: 'Give it a name.',
  title: 'Give it a title.',
};

export function friendlyError(error: unknown, fallback = "That didn't work. Try again."): string {
  if (error instanceof ReauthCancelledError) return "Okay, nothing was changed.";
  if (error instanceof PasskeyCancelledError) return "Passkey cancelled. Try again when you're ready.";
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
    if (status === 401) return isWeb() ? 'Your session ended. Sign in again.' : 'Your session ended. Pair this device again.';
    if (status === 403) return "You don't have permission to change this library.";
    if (status === 404) return "That's no longer here. Refresh and try again.";
    if (status === 412) return 'Someone changed this. Reload to see the latest.';
    if (status === 409) return problem.title ?? 'That conflicts with something that already exists.';
    if (status === 429) return 'Too many attempts. Wait a minute and try again.';
    return fallback;
  }
  // fetch itself failed: the server wasn't reachable
  return "Couldn't reach your server. Check your connection and try again.";
}
