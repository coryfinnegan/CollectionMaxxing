// Words for the offline answer (docs/06 copy rules: sentence case, no exclamation marks, say what to do next).

export const NOT_IN_LIBRARY = 'Not in your library';

export const NEVER_SYNCED = "Your library hasn't synced yet. Connect once to check what you own offline.";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "2 h ago": how long since the last complete sync. */
export function syncAge(syncedAt: number, now: number): string {
  const elapsed = Math.max(0, now - syncedAt);
  if (elapsed < MINUTE) return 'just now';
  if (elapsed < HOUR) return `${Math.floor(elapsed / MINUTE)} min ago`;
  if (elapsed < DAY) return `${Math.floor(elapsed / HOUR)} h ago`;
  return `${Math.floor(elapsed / DAY)} d ago`;
}

/** What the card says when the local copy answered, or null before the first sync has finished. */
export function staleCopyMessage(syncedAt: number | null, now: number): string {
  return syncedAt === null ? NEVER_SYNCED : `Based on your last sync, ${syncAge(syncedAt, now)}`;
}
