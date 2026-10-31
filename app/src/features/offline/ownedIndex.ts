// The phone's copy of "what this library has", and the synchronous lookup over it (docs/05 "Offline behavior").
// It is built from the snapshot endpoint's entries. Nothing here touches the network or storage.
//
// The copy holds every live item with its status. Owned, lent and missing are the library (isInCollection): they answer
// "You own this". A wishlisted or a sold item never does; it answers "On your wishlist" and "You sold this" instead.
import type { ItemStatus } from '@/api/generated/models';
import { isInCollection } from '@/features/library/statuses';
import { normalizeCode } from './normalize';

/** One copy in the library, as the offline answer shows it. */
export type OwnedCopy = {
  itemId: string;
  releaseId: string | null;
  workId: string | null;
  title: string | null;
  format: string | null;
  platform: string | null;
  locationPath: string | null;
  /** Owned, lent or missing: only the library's copies are ever listed as owned. */
  status: ItemStatus;
};

/**
 * What the snapshot endpoint sends for one item; a deleted entry is a tombstone and carries nothing else. `status` is null on a
 * tombstone, and on an entry saved before the snapshot carried it (read as owned).
 */
export type SnapshotEntry = Omit<OwnedCopy, 'status'> & {
  deleted: boolean;
  identifiers: string[];
  status: ItemStatus | null;
};

export type OwnedLookup = {
  /** Copies of the thing asked about, in the library (owned, lent, missing), oldest first. Empty means "Not in your library". */
  owned: OwnedCopy[];
  /** Copies of the same work in another format or on another platform, in the library, oldest first. */
  softMatches: OwnedCopy[];
  /** The item on the wishlist for the release asked about (the oldest), or null. Never part of `owned`. */
  wishlistItemId: string | null;
  /** The most recently added copy of the release asked about that was sold, traded or given away, or null. Never part of `owned`. */
  soldItemId: string | null;
};

/** Ask by barcode text (any form), by release, or by work. A bare string is a code. */
export type OwnedQuery = string | { code?: string | null; releaseId?: string | null; workId?: string | null };

export interface OwnedIndex {
  /** Answers from the local copy, synchronously. */
  lookup(query: OwnedQuery): OwnedLookup;
  /** Owned copies whose title contains every word of the query, best match first. The scanner's offline search. */
  searchTitles(query: string, limit?: number): OwnedCopy[];
  /** How many items in the library the copy holds (the wishlist and sold are not counted). */
  readonly size: number;
  /** Every entry, for saving. */
  entries(): SnapshotEntry[];
  /** A new index with these entries applied: deleted ones removed, the rest added or replaced. This one is unchanged. */
  apply(changes: readonly SnapshotEntry[]): OwnedIndex;
}

const EMPTY: OwnedLookup = { owned: [], softMatches: [], wishlistItemId: null, soldItemId: null };

const statusOf = (e: SnapshotEntry): ItemStatus => e.status ?? 'owned';
const inLibrary = (e: SnapshotEntry): boolean => isInCollection(statusOf(e));

const toCopy = (e: SnapshotEntry): OwnedCopy => ({
  itemId: e.itemId,
  releaseId: e.releaseId,
  workId: e.workId,
  title: e.title,
  format: e.format,
  platform: e.platform,
  locationPath: e.locationPath,
  status: statusOf(e),
});

const addTo = (map: Map<string, SnapshotEntry[]>, key: string, entry: SnapshotEntry) => {
  const list = map.get(key);
  if (list) list.push(entry);
  else map.set(key, [entry]);
};

class MapOwnedIndex implements OwnedIndex {
  private readonly byCode = new Map<string, SnapshotEntry[]>();
  private readonly byRelease = new Map<string, SnapshotEntry[]>();
  private readonly byWork = new Map<string, SnapshotEntry[]>();

  constructor(private readonly items: ReadonlyMap<string, SnapshotEntry>) {
    for (const entry of items.values()) {
      if (entry.releaseId) addTo(this.byRelease, entry.releaseId, entry);
      if (entry.workId) addTo(this.byWork, entry.workId, entry);
      const keys = new Set<string>();
      for (const identifier of entry.identifiers) {
        const key = normalizeCode(identifier);
        if (key) keys.add(key);
      }
      for (const key of keys) addTo(this.byCode, key, entry);
    }
  }

  get size() {
    let count = 0;
    for (const e of this.items.values()) if (inLibrary(e)) count += 1;
    return count;
  }

  entries() {
    return [...this.items.values()];
  }

  apply(changes: readonly SnapshotEntry[]): OwnedIndex {
    const next = new Map(this.items);
    for (const change of changes) {
      if (change.deleted) next.delete(change.itemId);
      else next.set(change.itemId, change);
    }
    return new MapOwnedIndex(next);
  }

  lookup(query: OwnedQuery): OwnedLookup {
    const { code, releaseId, workId } = typeof query === 'string' ? { code: query } : query;
    const matched = new Map<string, SnapshotEntry>();
    const take = (list: SnapshotEntry[] | undefined) => list?.forEach((e) => matched.set(e.itemId, e));

    if (releaseId) take(this.byRelease.get(releaseId));
    const key = code ? normalizeCode(code) : null;
    if (key) take(this.byCode.get(key));
    // Asking by work alone: every copy of the work is a copy of what was asked about. (A work has no wishlist or sold item of its own.)
    const byWorkOnly = !!workId && !releaseId && !key;
    if (byWorkOnly) take(this.byWork.get(workId));

    // Soft matches: other copies of the same work, in the library, that are not what was asked about.
    const works = new Set<string>();
    if (workId) works.add(workId);
    for (const e of matched.values()) if (e.workId) works.add(e.workId);
    const soft = new Map<string, SnapshotEntry>();
    for (const work of works) for (const e of this.byWork.get(work) ?? []) if (!matched.has(e.itemId) && inLibrary(e)) soft.set(e.itemId, e);

    // The wishlist and sold facts are about the release asked about: the oldest wishlist item, the newest sold copy.
    // Item ids are UUIDv7, so id order is age order (see below).
    let wishlistItemId: string | null = null;
    let soldItemId: string | null = null;
    if (!byWorkOnly) {
      for (const e of matched.values()) {
        if (e.status === 'wishlist' && (wishlistItemId === null || e.itemId < wishlistItemId)) wishlistItemId = e.itemId;
        if (e.status === 'sold' && (soldItemId === null || e.itemId > soldItemId)) soldItemId = e.itemId;
      }
    }
    for (const [itemId, e] of [...matched]) if (!inLibrary(e)) matched.delete(itemId);

    if (matched.size === 0 && soft.size === 0 && wishlistItemId === null && soldItemId === null) return EMPTY;
    // Item ids are UUIDv7, so ascending id order is oldest-first: the server's order (OwnershipQuery sorts copies by
    // created-at, then id). The index itself keeps sync order; only the answer is sorted, so the offline card and the
    // Move fallback name the same copy the server's copies[0] names.
    const byAge = (a: SnapshotEntry, b: SnapshotEntry) => {
      if (a.itemId === b.itemId) return 0;
      return a.itemId < b.itemId ? -1 : 1;
    };
    const owned = [...matched.values()].sort(byAge).map(toCopy);
    const softMatches = [...soft.values()].sort(byAge).map(toCopy);
    return { owned, softMatches, wishlistItemId, soldItemId };
  }

  searchTitles(query: string, limit = 20): OwnedCopy[] {
    const words = query.toLowerCase().split(/\s+/).filter(Boolean);
    if (words.length === 0) return [];
    const scored: { copy: OwnedCopy; score: number }[] = [];
    for (const e of this.items.values()) {
      if (!inLibrary(e)) continue; // the scanner's offline search finds what you have, not what you want or sold
      const title = (e.title ?? '').toLowerCase();
      if (title.length === 0 || !words.every((w) => title.includes(w))) continue;
      // An exact title wins; otherwise the shorter title (fewer extra words) wins, then A to Z.
      const score = title === words.join(' ') ? 0 : title.length;
      scored.push({ copy: toCopy(e), score });
    }
    scored.sort((a, b) => a.score - b.score || (a.copy.title ?? '').localeCompare(b.copy.title ?? ''));
    return scored.slice(0, Math.max(1, limit)).map((s) => s.copy);
  }
}

/** Builds an index from entries; deleted entries are ignored. */
export function createOwnedIndex(entries: readonly SnapshotEntry[] = []): OwnedIndex {
  return new MapOwnedIndex(new Map<string, SnapshotEntry>()).apply(entries);
}
