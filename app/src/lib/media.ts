// This server's own served images (covers, crops, artwork): recognizing them so the phone can ask for them with its sign-in.
// Only what ownMediaUrl returns may carry the sign-in, and only to the address it returns.
import { baseUrl } from './auth/session';

/** What the server's media route can name: letters, digits and a few marks. No escapes, so no `%2e%2e` or `%2f` can hide a path change. */
const SEGMENT = /^[A-Za-z0-9._~-]+$/;

/**
 * The absolute address of one of this server's own served images, or undefined when the value is not one. Accepts the
 * relative `/media/...` path the DTOs carry and the absolute form callers already built. The path must be a normalized one
 * under `/media/`: no `..` or `.` segment, no escapes, no backslash, no query, nothing else. A third-party address, a local
 * file, or any other route on this server (such as `/api/...`) is never recognized: the phone does not talk to third parties,
 * and the token does not go to them or to routes the server did not mean for artwork.
 */
export function ownMediaUrl(url: string): string | undefined {
  const base = baseUrl();
  const path = url.startsWith('/') ? url : url.startsWith(`${base}/`) ? url.slice(base.length) : null;
  if (!path?.startsWith('/media/')) return undefined;
  const segments = path.slice('/media/'.length).split('/');
  const ok = segments.every((segment) => SEGMENT.test(segment) && segment !== '.' && segment !== '..');
  return ok ? `${base}${path}` : undefined;
}
