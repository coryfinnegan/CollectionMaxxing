// Turns an API failure (Problem Details) into friendly copy: what happened and what to do next.
import { ApiError } from './http';

const fieldCopy: Record<string, string> = {
  name: 'Give it a name.',
};

export function friendlyError(error: unknown, fallback = "That didn't work. Try again."): string {
  if (error instanceof ApiError) {
    const { status, problem } = error;
    if (status === 400 && problem.errors) {
      const field = Object.keys(problem.errors)[0];
      if (field) return fieldCopy[field.toLowerCase()] ?? problem.errors[field]?.[0] ?? fallback;
    }
    if (status === 401) return 'Your session ended. Pair this device again.';
    if (status === 403) return "You don't have permission to change this library.";
    if (status === 404) return "That's no longer here. Refresh and try again.";
    if (status === 409) return problem.title ?? 'That conflicts with something that already exists.';
    return fallback;
  }
  // fetch itself failed: the server wasn't reachable
  return "Couldn't reach your server. Check your connection and try again.";
}
