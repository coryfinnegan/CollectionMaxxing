// Jest's stand-in for react-native-auth0 (the real one needs the native module). Tests script `mockAuth0` through
// `jest.requireMock('react-native-auth0').mockAuth0`; `resetMockAuth0()` puts every function back to a harmless default.
export const mockAuth0 = {
  webAuth: { authorize: jest.fn(), clearSession: jest.fn() },
  credentialsManager: {
    hasValidCredentials: jest.fn(),
    getCredentials: jest.fn(),
    saveCredentials: jest.fn(),
    clearCredentials: jest.fn(),
  },
  auth: { revoke: jest.fn() },
};

export function resetMockAuth0() {
  mockAuth0.webAuth.authorize.mockReset().mockResolvedValue({ accessToken: 'a', refreshToken: 'r', idToken: 'i', tokenType: 'Bearer', expiresAt: 0 });
  mockAuth0.webAuth.clearSession.mockReset().mockResolvedValue(undefined);
  mockAuth0.credentialsManager.hasValidCredentials.mockReset().mockResolvedValue(false);
  mockAuth0.credentialsManager.getCredentials.mockReset().mockResolvedValue({ accessToken: 'a', refreshToken: 'r' });
  mockAuth0.credentialsManager.saveCredentials.mockReset().mockResolvedValue(undefined);
  mockAuth0.credentialsManager.clearCredentials.mockReset().mockResolvedValue(undefined);
  mockAuth0.auth.revoke.mockReset().mockResolvedValue(undefined);
}
resetMockAuth0();

export default class Auth0 {
  webAuth = mockAuth0.webAuth;
  credentialsManager = mockAuth0.credentialsManager;
  auth = mockAuth0.auth;
}
