// Connects the HTTP client's auth hooks to the router and the passkey prompt. Called once from the root layout.
import { configureAuth } from '@/lib/auth/session';
import { signInWithPasskey } from './flows';
import { getPasskeys } from './services';

type Navigator = { replace: (href: '/pair' | '/login') => void };

// The pages that are meant to be reached while signed out.
const signedOutPaths = ['/setup', '/login', '/join'];

export function installAuth(router: Navigator) {
  configureAuth({
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
