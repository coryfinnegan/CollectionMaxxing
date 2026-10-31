// Delta sync for the offline copy. The first sync pulls everything; later ones pull since the saved cursor. Each page is
// applied and saved as one step, and the cursor moves with it, so a page that fails to arrive or to save changes nothing.
import { getOwnershipSnapshot } from '@/api/generated/ownership/ownership';
import { ApiError } from '@/lib/http';
import { getOwnedCache, type OwnedCache } from './ownedCache';
import { createOwnedIndex, type OwnedIndex, type SnapshotEntry } from './ownedIndex';

export type SnapshotPage = { entries: SnapshotEntry[]; nextCursor: string; hasMore: boolean };
export type FetchPage = (args: { since: string | null }) => Promise<SnapshotPage>;

export type SyncResult = { ok: true; pages: number; changed: number } | { ok: false; error: unknown };

const PAGE_LIMIT = 500;
// Stops a server that always says "more" from keeping the phone busy forever.
const MAX_PAGES = 200;

/** Reads the generated client's page into what the index holds (the shapes match; this guards a bad answer). */
export const fetchSnapshotPage =
  (libraryId: string): FetchPage =>
  async ({ since }) => {
    const response = await getOwnershipSnapshot(libraryId, { ...(since ? { since } : {}), limit: PAGE_LIMIT });
    if (response.status !== 200) throw new ApiError(response.status, {});
    const page = response.data;
    if (!page || !Array.isArray(page.entries) || typeof page.nextCursor !== 'string') {
      throw new Error('The ownership snapshot was not in the expected shape.');
    }
    return {
      nextCursor: page.nextCursor,
      hasMore: page.hasMore,
      entries: page.entries.map((e) => ({
        itemId: e.itemId,
        deleted: e.deleted,
        releaseId: e.releaseId ?? null,
        workId: e.workId ?? null,
        title: e.title ?? null,
        format: e.format ?? null,
        platform: e.platform ?? null,
        identifiers: e.identifiers ?? [],
        locationPath: e.locationPath ?? null,
        status: e.status ?? null,
      })),
    };
  };

const inFlight = new WeakMap<OwnedCache, Promise<SyncResult>>();

/**
 * Brings the cache up to date. Never throws: a failed sync leaves the previous copy intact and returns the error so the
 * caller can retry quietly. Calls made while a sync is running share it.
 */
export function syncOwned(cache: OwnedCache, fetchPage: FetchPage, now: () => number = Date.now): Promise<SyncResult> {
  const running = inFlight.get(cache);
  if (running) return running;
  const run = doSync(cache, fetchPage, now).finally(() => inFlight.delete(cache));
  inFlight.set(cache, run);
  return run;
}

/** Syncs one library against the real endpoint. */
export const syncLibrary = (libraryId: string, now?: () => number) =>
  syncOwned(getOwnedCache(libraryId), fetchSnapshotPage(libraryId), now);

async function doSync(cache: OwnedCache, fetchPage: FetchPage, now: () => number): Promise<SyncResult> {
  let pages = 0;
  let changed = 0;
  let resetOnce = false;
  // The copy the last release saved answers (as all in the library) until a whole new one has arrived: its pages gather here and
  // are saved together, so a sync that dies partway leaves the old copy whole instead of replacing it with a half-new one.
  let aside: { index: OwnedIndex; cursor: string | null } | null = cache.state.legacy ? { index: createOwnedIndex(), cursor: null } : null;
  try {
    for (;;) {
      const { index, cursor, syncedAt } = cache.state;
      const since = aside ? aside.cursor : cursor;
      let page: SnapshotPage;
      try {
        page = await fetchPage({ since });
      } catch (error) {
        // A cursor the server no longer accepts: forget the copy and pull everything once.
        if (since && !resetOnce && error instanceof ApiError && error.status === 400) {
          resetOnce = true;
          if (aside) aside = { index: createOwnedIndex(), cursor: null };
          else cache.clear();
          continue;
        }
        throw error;
      }
      pages += 1;
      if (pages > MAX_PAGES) throw new Error('The ownership snapshot did not finish.');
      changed += page.entries.length;
      if (aside) {
        aside = { index: aside.index.apply(page.entries), cursor: page.nextCursor };
        if (!page.hasMore) {
          cache.commit({ index: aside.index, cursor: aside.cursor, syncedAt: now() });
          return { ok: true, pages, changed };
        }
        continue;
      }
      cache.commit({
        index: index.apply(page.entries),
        cursor: page.nextCursor,
        syncedAt: page.hasMore ? syncedAt : now(),
      });
      if (!page.hasMore) return { ok: true, pages, changed };
    }
  } catch (error) {
    return { ok: false, error };
  }
}
