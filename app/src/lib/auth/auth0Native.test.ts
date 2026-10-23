import * as sdk from 'react-native-auth0';
import { createAuth0Native } from './auth0Native.native';
import { AuthLostError } from './native';

// The Jest stand-in in app/__mocks__ (import it like this: requireMock would hand back a second copy).
const { mockAuth0, resetMockAuth0 } = sdk as unknown as {
  mockAuth0: Record<string, Record<string, jest.Mock>>;
  resetMockAuth0: () => void;
};
const cm = mockAuth0.credentialsManager;

beforeEach(() => {
  resetMockAuth0();
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});
afterEach(() => jest.restoreAllMocks());

describe('Auth0 on the iPhone', () => {
  it('asks for the API audience and offline access, and keeps the credentials', async () => {
    const credentials = { accessToken: 'a', refreshToken: 'r' };
    mockAuth0.webAuth.authorize.mockResolvedValue(credentials);

    await createAuth0Native().signIn();

    expect(mockAuth0.webAuth.authorize.mock.calls[0][0]).toEqual({
      scope: 'openid profile email offline_access',
      audience: 'urn:collectionmaxxing:api',
    });
    expect(cm.saveCredentials).toHaveBeenCalledWith(credentials);
  });

  it('a person backing out of the sign-in sheet rejects and stores nothing', async () => {
    mockAuth0.webAuth.authorize.mockRejectedValue(new Error('a0.session.user_cancelled'));
    await expect(createAuth0Native().signIn()).rejects.toThrow();
    expect(cm.saveCredentials).not.toHaveBeenCalled();
  });

  it('hasValidCredentials follows the Credentials Manager', async () => {
    cm.hasValidCredentials.mockResolvedValue(true);
    expect(await createAuth0Native().hasValidCredentials()).toBe(true);
    cm.hasValidCredentials.mockResolvedValue(false);
    expect(await createAuth0Native().hasValidCredentials()).toBe(false);
  });

  it('getAccessToken returns the manager’s token and can force a refresh', async () => {
    cm.getCredentials.mockResolvedValue({ accessToken: 'fresh' });
    expect(await createAuth0Native().getAccessToken(true)).toBe('fresh');
    expect(cm.getCredentials.mock.calls[0][3]).toBe(true);
  });

  it.each([
    ['no stored credentials', { type: 'NO_CREDENTIALS' }],
    ['a refresh token Auth0 rejects', { code: 'invalid_grant' }],
  ])('%s is a lost sign-in', async (_name, error) => {
    cm.getCredentials.mockRejectedValue(error);
    await expect(createAuth0Native().getAccessToken()).rejects.toBeInstanceOf(AuthLostError);
  });

  it.each([
    ['a network error', new Error('Network request failed')],
    ['a 429', { status: 429 }],
    ['a 503', { status: 503 }],
  ])('%s is passed on as it is, never as a lost sign-in', async (_name, error) => {
    cm.getCredentials.mockRejectedValue(error);
    await expect(createAuth0Native().getAccessToken()).rejects.toBe(error);
    expect(cm.clearCredentials).not.toHaveBeenCalled();
  });

  it('sign out revokes the refresh token first, then clears the credentials, then ends the browser session', async () => {
    const order: string[] = [];
    cm.getCredentials.mockResolvedValue({ accessToken: 'a', refreshToken: 'r-1' });
    mockAuth0.auth.revoke.mockImplementation(async () => void order.push('revoke'));
    cm.clearCredentials.mockImplementation(async () => void order.push('clear'));
    mockAuth0.webAuth.clearSession.mockImplementation(async () => void order.push('clearSession'));

    const result = await createAuth0Native().signOut();

    expect(mockAuth0.auth.revoke).toHaveBeenCalledWith({ refreshToken: 'r-1' });
    expect(order).toEqual(['revoke', 'clear', 'clearSession']);
    expect(result).toEqual({ revoked: true });
  });

  it('sign out still clears everything on this iPhone when the revoke fails on the network', async () => {
    mockAuth0.auth.revoke.mockRejectedValue(new Error('Network request failed'));

    const result = await createAuth0Native().signOut();

    expect(result).toEqual({ revoked: false });
    expect(cm.clearCredentials).toHaveBeenCalled();
    expect(mockAuth0.webAuth.clearSession).toHaveBeenCalled();
  });

  it('sign out still works when the credentials cannot be read at all', async () => {
    cm.getCredentials.mockRejectedValue(new Error('Network request failed'));

    const result = await createAuth0Native().signOut();

    expect(mockAuth0.auth.revoke).not.toHaveBeenCalled();
    expect(result).toEqual({ revoked: false });
    expect(cm.clearCredentials).toHaveBeenCalled();
  });

  it('closing the sign-out sheet does not fail the sign out', async () => {
    mockAuth0.webAuth.clearSession.mockRejectedValue(new Error('a0.session.user_cancelled'));
    await expect(createAuth0Native().signOut()).resolves.toEqual({ revoked: true });
  });
});
