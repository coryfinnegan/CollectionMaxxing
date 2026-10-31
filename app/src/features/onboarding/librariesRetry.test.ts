import { ApiError } from '@/lib/http';
import { librariesRetry, librariesRetryDelay } from './librariesRetry';

describe('librariesRetry', () => {
  it('tries a failed list twice more, then gives up', () => {
    const failed = new ApiError(503, {});
    expect(librariesRetry(0, failed)).toBe(true);
    expect(librariesRetry(1, failed)).toBe(true);
    expect(librariesRetry(2, failed)).toBe(false);
  });

  it('retries a network failure, which has no status', () => {
    expect(librariesRetry(0, new TypeError('Network request failed'))).toBe(true);
  });

  it.each([401, 403, 404])('does not retry a %s, which asking again will not change', (status) => {
    expect(librariesRetry(0, new ApiError(status, {}))).toBe(false);
  });

  it.each([408, 429])('still retries a %s, which clears by itself', (status) => {
    expect(librariesRetry(0, new ApiError(status, {}))).toBe(true);
  });
});

describe('librariesRetryDelay', () => {
  it('waits a second, then two, and never more than four', () => {
    expect([0, 1, 2, 3, 8].map(librariesRetryDelay)).toEqual([1000, 2000, 4000, 4000, 4000]);
  });
});
