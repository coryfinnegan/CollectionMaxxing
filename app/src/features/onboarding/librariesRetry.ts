// How the libraries list tries again. Every screen waits on this one answer, so a blip should not strand them,
// and a real failure should still be said out loud soon (about three seconds in).
import { ApiError } from '@/lib/http';

const RETRIES = 2;

/** An answer that asking again will not change (signed out, no access, gone) is not retried; the rest are. */
export function librariesRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429) {
    return false;
  }
  return failureCount < RETRIES;
}

/** One second, then two, never more than four. */
export const librariesRetryDelay = (failureCount: number): number => Math.min(1000 * 2 ** failureCount, 4000);
