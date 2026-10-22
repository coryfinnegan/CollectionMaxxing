// Connects the HTTP client's auth hooks to the router and the sign-in. Called once from the root layout.
import * as SecureStore from 'expo-secure-store';
import { Platform } from 'react-native';
import { configureAuth } from '@/lib/auth/session';
import { SecureTokenStore } from '@/lib/auth/tokenStore';
import { requestReauth } from './reauth';

type Navigator = { replace: (href: '/pair' | '/login') => void };

// The pages that are meant to be reached while signed out.
const signedOutPaths = ['/setup', '/login', '/join', '/account/not-linked'];

export function installAuth(router: Navigator) {
  configureAuth({
    // The phone keeps its refresh token in the Keychain; the web app uses the session cookie and stores nothing.
    ...(Platform.OS === 'ios' ? { tokenStore: new SecureTokenStore(SecureStore, { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY }) } : {}),
    onAuthLost: (destination) => {
      const path = typeof window !== 'undefined' ? (window.location?.pathname ?? '') : '';
      if (destination === 'login' && signedOutPaths.some((p) => path.startsWith(p))) return;
      router.replace(destination === 'pair' ? '/pair' : '/login');
    },
    // Web: explain, then send the browser through a fresh sign-in and back (see ./reauth).
    reauth: requestReauth,
  });
}
