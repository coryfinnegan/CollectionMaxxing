// What a person pastes or types into "Join a library": a full link, a link token, or a short code. Case and dashes don't matter,
// and Crockford base32's look-alikes (O for 0, I and L for 1) count as the same (docs/15 "Invite codes").

const CROCKFORD = /^[0-9A-HJKMNP-TV-Z]{10}$/;

/** `ABCD-EFGH-JK` for ten code characters, else null. */
export function normalizeCode(input: string): string | null {
  const squashed = input
    .replace(/[\s-]/g, '')
    .toUpperCase()
    .replace(/O/g, '0')
    .replace(/[IL]/g, '1');
  if (!CROCKFORD.test(squashed)) return null;
  return `${squashed.slice(0, 4)}-${squashed.slice(4, 8)}-${squashed.slice(8)}`;
}

/** The token at the end of a join link, or null when the text isn't one. */
function tokenFromLink(input: string): string | null {
  const match = /\/join\/([^/?#\s]+)/i.exec(input);
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

/** What to send as `invite`: the code in its standard form, the token from a link, or a bare token. Null when it is none of these. */
export function parseInvite(raw: string): string | null {
  const input = raw.trim();
  if (!input) return null;
  const fromLink = tokenFromLink(input);
  if (fromLink) return normalizeCode(fromLink) ?? fromLink;
  const code = normalizeCode(input);
  if (code) return code;
  return /^[A-Za-z0-9_-]{8,}$/.test(input) ? input : null;
}
