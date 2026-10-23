import { isDefiniteAuthFailure } from './native';

describe('which failures mean the sign-in is gone', () => {
  it.each([
    ['Auth0 refusing the refresh token', { code: 'invalid_grant', message: 'Unknown or invalid refresh token.' }],
    ['a refresh error body that says invalid_grant', { json: { error: 'invalid_grant' } }],
    ['no stored credentials', { type: 'NO_CREDENTIALS', message: 'No Credentials were previously set.' }],
    ['no refresh token to renew with', { type: 'NO_REFRESH_TOKEN' }],
    ['a revoked refresh token', new Error('The refresh token was revoked')],
  ])('%s is definite', (_name, error) => {
    expect(isDefiniteAuthFailure(error)).toBe(true);
  });

  it.each([
    ['a network error', new Error('Network request failed')],
    ['no network type', { type: 'NO_NETWORK', message: 'no_network' }],
    ['a timeout', new Error('The request timed out')],
    ['a 429', { status: 429, message: 'Too many requests' }],
    ['a rate limit', { code: 'too_many_requests' }],
    ['a 503', { status: 503, message: 'Service Unavailable' }],
    ['a renew that failed for an unknown reason', { type: 'RENEW_FAILED', message: 'Failed to renew' }],
    ['an unreadable error', undefined],
    ['a plain string', 'boom'],
  ])('%s is not', (_name, error) => {
    expect(isDefiniteAuthFailure(error)).toBe(false);
  });
});
