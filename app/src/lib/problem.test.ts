import { ApiError } from './http';
import { logger } from './auth/log';
import { friendlyError } from './problem';

jest.mock('./auth/log', () => ({ logger: { warn: jest.fn(), error: jest.fn() } }));

const loggedErrors = () => jest.mocked(logger.error).mock.calls.map(([message]) => message);

beforeEach(() => jest.clearAllMocks());

describe('friendlyError', () => {
  it('blames the connection only for a real network failure', () => {
    expect(friendlyError(new TypeError('Network request failed'))).toBe(
      "Couldn't reach your server. Check your connection and try again.",
    );
    expect(logger.error).not.toHaveBeenCalled();
  });

  it('reports a programming error generically and logs the real error', () => {
    expect(friendlyError(new Error('boom'))).toBe('Something went wrong. Try again.');

    expect(loggedErrors()).toHaveLength(1);
    expect(loggedErrors()[0]).toContain('boom');
    expect(loggedErrors()[0]).toContain('Error');
  });

  it('reports a thrown non-error generically and logs it', () => {
    expect(friendlyError('kaboom')).toBe('Something went wrong. Try again.');

    expect(loggedErrors()).toHaveLength(1);
    expect(loggedErrors()[0]).toContain('kaboom');
  });

  it('keeps turning API failures into their friendly copy', () => {
    expect(friendlyError(new ApiError(404, { title: 'Not found' }))).toBe("That's no longer here. Refresh and try again.");
    expect(friendlyError(new ApiError(409, { title: 'Already in your library.' }))).toBe('Already in your library.');

    expect(logger.error).not.toHaveBeenCalled();
  });

  it('keeps the caller fallback for API failures without their own copy', () => {
    expect(friendlyError(new ApiError(500, { title: 'Broken' }))).toBe("That didn't work. Try again.");
    expect(friendlyError(new ApiError(500, { title: 'Broken' }), 'Saving failed. Try again.')).toBe('Saving failed. Try again.');

    expect(logger.error).not.toHaveBeenCalled();
  });
});
