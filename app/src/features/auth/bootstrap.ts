// Connects the HTTP client's auth hooks to the router and the sign-in (the phone's Auth0 sign-in itself is created in lib/auth/session). Called once from the root layout.
import { configureAuth } from '@/lib/auth/session';
import { requestReauth } from './reauth';

type Navigator = { replace: (href: '/login' | '/welcome') => void };

// The pages that are meant to be reached while signed out.
const signedOutPaths = ['/login', '/join'];

export function installAuth(router: Navigator) {
  configureAuth({
    onAuthLost: (destination) => {
      const path = typeof window !== 'undefined' ? (window.location?.pathname ?? '') : '';
      if (destination === 'login' && signedOutPaths.some((p) => path.startsWith(p))) return;
      // Signed in with an account that has no library yet: Welcome (every sign-in is an account, docs/15).
      router.replace(destination === 'not-linked' ? '/welcome' : '/login');
    },
    // Web: explain, then send the browser through a fresh sign-in and back (see ./reauth).
    reauth: requestReauth,
  });
}
