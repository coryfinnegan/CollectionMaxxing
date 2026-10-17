// Backup and restore (docs/04 "Catalogue", feature-prints): the prints go to the person's own PC, opaque to the server, so a new phone
// can pull them back. Backup is for editors; a viewer keeps prints local only. Nothing but the print, ids, revision and time is sent.
import { listFeaturePrints, putFeaturePrints } from '@/api/generated/catalog/catalog';
import { ApiError } from '@/lib/http';
import type { PrintVision } from './matcher';
import type { FingerprintStore, StoredPrint } from './store';

/** The route takes at most this many prints in a call and gives at most this many in a page. */
export const BATCH_SIZE = 200;

export type WirePrint = { itemId: string; releaseId: string; revision: number; data: string; createdAt: string };

export interface BackupRemote {
  /** All or nothing. Throws when the PC cannot be reached or refuses. */
  put(prints: WirePrint[]): Promise<void>;
  list(args: { since?: string; cursor?: string; limit: number }): Promise<{ items: WirePrint[]; nextCursor: string | null }>;
}

export const restBackup = (libraryId: string): BackupRemote => ({
  async put(prints) {
    const response = await putFeaturePrints(libraryId, { prints });
    if (response.status !== 204) throw new ApiError(response.status, {});
  },
  async list({ since, cursor, limit }) {
    const response = await listFeaturePrints(libraryId, { ...(since ? { since } : {}), ...(cursor ? { cursor } : {}), limit });
    if (response.status !== 200) throw new ApiError(response.status, {});
    const page = response.data;
    if (!page || !Array.isArray(page.items)) throw new Error('The feature-print page was not in the expected shape.');
    return { items: page.items, nextCursor: page.nextCursor ?? null };
  },
});

const toWire = (row: StoredPrint): WirePrint => ({
  itemId: row.itemId,
  releaseId: row.releaseId,
  revision: row.revision,
  data: row.print,
  createdAt: row.createdAt,
});

const fromWire = (row: WirePrint): StoredPrint => ({
  itemId: row.itemId,
  releaseId: row.releaseId,
  revision: row.revision,
  print: row.data,
  createdAt: row.createdAt,
  synced: true,
});

export type BackupResult = { ok: true; sent: number } | { ok: false; sent: number; error: unknown };

/**
 * Uploads the prints the PC does not have, oldest first, at most 200 a call, marking each batch synced as it lands. A failed batch
 * stops the run and stays unsynced, so the next run sends it again; the batches before it stay synced. A viewer sends nothing.
 */
export async function backupPrints({ store, canEdit, remote }: { store: FingerprintStore; canEdit: boolean; remote: BackupRemote }): Promise<BackupResult> {
  if (!canEdit) return { ok: true, sent: 0 };
  const waiting = store.unsynced().sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  let sent = 0;
  for (let at = 0; at < waiting.length; at += BATCH_SIZE) {
    const batch = waiting.slice(at, at + BATCH_SIZE);
    try {
      await remote.put(batch.map(toWire));
    } catch (error) {
      return { ok: false, sent, error };
    }
    store.markSynced(batch.map((r) => r.itemId));
    sent += batch.length;
  }
  return { ok: true, sent };
}

/** A server that always says "more" must not keep the phone busy forever. */
const MAX_PAGES = 500;

export type RestoreResult = { ok: true; pulled: number } | { ok: false; pulled: number; error: unknown };

/**
 * Pulls the prints written since the last restore, page by page. The restore point moves only when every page arrived, so a failure
 * midway simply starts again. A print this phone has not backed up yet is never overwritten.
 */
export async function restorePrints({ store, remote }: { store: FingerprintStore; remote: BackupRemote }): Promise<RestoreResult> {
  const since = store.restoredThrough ?? undefined;
  let cursor: string | undefined;
  let newest = store.restoredThrough;
  let pulled = 0;
  try {
    for (let pages = 0; pages < MAX_PAGES; pages++) {
      const page = await remote.list({ ...(since ? { since } : {}), ...(cursor ? { cursor } : {}), limit: BATCH_SIZE });
      store.merge(page.items.map(fromWire));
      pulled += page.items.length;
      for (const item of page.items) if (!newest || item.createdAt > newest) newest = item.createdAt;
      if (!page.nextCursor) {
        if (newest !== store.restoredThrough) store.setRestoredThrough(newest);
        return { ok: true, pulled };
      }
      cursor = page.nextCursor;
    }
    throw new Error('The feature prints did not finish.');
  } catch (error) {
    return { ok: false, pulled, error };
  }
}

/**
 * A removed item's print is dropped on the next sync: once the offline copy of what the library owns has synced, a print whose item
 * is not in it goes. A print newer than that sync is kept (its item may simply be too new to be in the copy). Returns how many went.
 */
export function dropRemovedItems({ store, ownedItemIds, syncedAt }: { store: FingerprintStore; ownedItemIds: ReadonlySet<string>; syncedAt: number | null }): number {
  if (syncedAt === null) return 0;
  const gone = store
    .all()
    .filter((r) => !ownedItemIds.has(r.itemId) && Date.parse(r.createdAt) < syncedAt)
    .map((r) => r.itemId);
  store.remove(gone);
  return gone.length;
}

/**
 * Prints of another revision than this phone makes are kept but never matched. Where the item's cover can be fetched, its print is made
 * again at this phone's revision (and backed up again). Best effort: no cover, or no print, skips that item silently.
 */
export async function recomputeStale({
  store,
  vision,
  cover,
  discard,
  limit = Infinity,
}: {
  store: FingerprintStore;
  vision: PrintVision;
  /** Fetches the release's cover into the phone's temp storage and returns where it is, or undefined when it cannot. */
  cover: (releaseId: string) => Promise<string | undefined>;
  /** Deletes a fetched cover once it has been printed. */
  discard: (uri: string) => Promise<void>;
  /** The most covers to fetch in one run. */
  limit?: number;
}): Promise<void> {
  let attempts = 0;
  for (const row of store.all()) {
    const revision = store.deviceRevision;
    if (revision !== null && row.revision === revision) continue;
    if (attempts++ >= limit) return;
    let uri: string | undefined;
    try {
      uri = await cover(row.releaseId);
      if (!uri) continue;
      const print = await vision.featurePrint(uri);
      store.setDeviceRevision(print.revision);
      if (print.revision !== row.revision) store.put({ ...row, revision: print.revision, print: print.data, synced: false });
    } catch {
      // skipped silently: the old print stays, unmatched
    } finally {
      if (uri) await discard(uri).catch(() => undefined);
    }
  }
}
