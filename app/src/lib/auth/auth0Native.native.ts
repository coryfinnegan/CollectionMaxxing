// iPhone sign-in with react-native-auth0 (docs/14 "iPhone"). Auth0's Credentials Manager keeps the tokens in the Keychain and
// refreshes the access token itself; this file only maps it onto NativeAuth. Metro picks it over ./auth0Native.ts on iOS.
import Auth0 from 'react-native-auth0';
import { logger } from './log';
import { AuthLostError, type NativeAuth, isDefiniteAuthFailure } from './native';

export const AUTH0_AUDIENCE = 'urn:collectionmaxxing:api';
export const AUTH0_SCOPE = 'openid profile email offline_access';
/** Must match `customScheme` in the react-native-auth0 plugin of app.config.ts. */
const CUSTOM_SCHEME = 'io.github.coryfinnegan.collectionmaxxing.auth0';

export function createAuth0Native(): NativeAuth {
  const auth0 = new Auth0({
    domain: process.env.EXPO_PUBLIC_AUTH0_DOMAIN ?? '',
    clientId: process.env.EXPO_PUBLIC_AUTH0_CLIENT_ID ?? '',
  });
  const manager = auth0.credentialsManager;

  return {
    async hasValidCredentials() {
      try {
        return await manager.hasValidCredentials();
      } catch {
        return false;
      }
    },

    async signIn() {
      const credentials = await auth0.webAuth.authorize({ scope: AUTH0_SCOPE, audience: AUTH0_AUDIENCE }, { customScheme: CUSTOM_SCHEME });
      await manager.saveCredentials(credentials);
    },

    async getAccessToken(forceRefresh = false) {
      try {
        const credentials = await manager.getCredentials(AUTH0_SCOPE, undefined, undefined, forceRefresh);
        if (!credentials?.accessToken) throw new AuthLostError();
        return credentials.accessToken;
      } catch (e) {
        if (e instanceof AuthLostError) throw e;
        if (isDefiniteAuthFailure(e)) throw new AuthLostError();
        throw e;
      }
    },

    async signOut() {
      let revoked = false;
      try {
        const { refreshToken } = await manager.getCredentials(AUTH0_SCOPE);
        if (refreshToken) {
          await auth0.auth.revoke({ refreshToken });
          revoked = true;
        }
      } catch {
        logger.warn('Could not revoke the sign-in at Auth0; clearing it on this iPhone anyway.');
      }
      try {
        await manager.clearCredentials();
      } catch {
        logger.warn('Could not clear the stored sign-in.');
      }
      try {
        await auth0.webAuth.clearSession({}, { customScheme: CUSTOM_SCHEME });
      } catch {
        // The person closed the sign-out sheet, or there is no browser session left. This iPhone is signed out either way.
      }
      return { revoked };
    },

    async clear() {
      try {
        await manager.clearCredentials();
      } catch {
        logger.warn('Could not clear the stored sign-in.');
      }
    },
  };
}
