// Connects the HTTP client's auth hooks to the router and the passkey prompt. Called once from the root layout.
import * as SecureStore from 'expo-secure-store';
import { Platform } from 'react-native';
import { configureAuth } from '@/lib/auth/session';
import { SecureTokenStore } from '@/lib/auth/tokenStore';
import { signInWithPasskey } from './flows';
import { getPasskeys } from './services';

type Navigator = { replace: (href: '/pair' | '/login') => void };

// The pages that are meant to be reached while signed out.
const signedOutPaths = ['/setup', '/login', '/join'];

export function installAuth(router: Navigator) {
  configureAuth({
    // The phone keeps its refresh token in the Keychain; the web app uses the session cookie and stores nothing.
    ...(Platform.OS === 'ios' ? { tokenStore: new SecureTokenStore(SecureStore) } : {}),
    onAuthLost: (destination) => {
      const path = typeof window !== 'undefined' ? (window.location?.pathname ?? '') : '';
      if (destination === 'login' && signedOutPaths.some((p) => path.startsWith(p))) return;
      router.replace(destination === 'pair' ? '/pair' : '/login');
    },
    reauth: async () => {
      await signInWithPasskey(getPasskeys());
    },
  });
}
