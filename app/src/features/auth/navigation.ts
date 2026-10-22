// Sign-in is a browser navigation, not a fetch: these build the URLs and do the navigating. No tokens reach the browser.
import type { SignInError } from './contract';
import { getNavigator } from './services';

const DEFAULT_RETURN = '/library';

/** A path on this site (`/library?x=1`), or the fallback. Never an address on another host. */
export function safeReturnUrl(value: unknown, fallback = DEFAULT_RETURN): string {
  return typeof value === 'string' && /^\/(?![/\\])/.test(value) ? value : fallback;
}

/** Where the person is now, as a local path, without a leftover `error` from an earlier attempt. */
export function currentLocalUrl(): string {
  const location = (globalThis as { location?: { pathname?: string; search?: string } }).location;
  if (!location?.pathname) return DEFAULT_RETURN;
  const params = new URLSearchParams(location.search ?? '');
  params.delete('error');
  const query = params.toString();
  return `${location.pathname}${query ? `?${query}` : ''}`;
}

export type LoginOptions = { returnUrl?: string; reauth?: boolean; link?: boolean; invite?: string };

export function loginUrl({ returnUrl, reauth, link, invite }: LoginOptions = {}): string {
  const params = new URLSearchParams();
  if (returnUrl) params.set('returnUrl', returnUrl);
  if (reauth) params.set('reauth', '1');
  if (link) params.set('link', '1');
  if (invite) params.set('invite', invite);
  const query = params.toString();
  return `/account/login${query ? `?${query}` : ''}`;
}

export const navigateTo = (url: string) => getNavigator()(url);

/** Copy for `?error=` on the sign-in page and on the page a link or confirmation returns to. */
const errorCopy: Record<SignInError, string> = {
  signin_failed: "Sign-in didn't finish. Try again.",
  invite_invalid: "This invite link isn't valid anymore. Ask for a new one.",
  setup_closed: 'Setup is already finished. Sign in instead.',
  already_linked: 'That account is already used by someone else here. Choose a different one.',
  reauth_failed: "We couldn't confirm it's you. Try again.",
};

export function signInErrorMessage(code: unknown): string | null {
  return typeof code === 'string' && code in errorCopy ? errorCopy[code as SignInError] : null;
}
