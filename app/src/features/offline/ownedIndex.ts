// The phone's copy of "what this library owns", and the synchronous lookup over it (docs/05 "Offline behavior").
// It is built from the snapshot endpoint's entries. Nothing here touches the network or storage.
import { normalizeCode } from './normalize';

/** One owned copy, as the offline answer shows it. */
export type OwnedCopy = {
  itemId: string;
  releaseId: string | null;
  workId: string | null;
  title: string | null;
  format: string | null;
  platform: string | null;
  locationPath: string | null;
};

/** What the snapshot endpoint sends for one item; a deleted entry is a tombstone and carries nothing else. */
export type SnapshotEntry = OwnedCopy & {
  deleted: boolean;
  identifiers: string[];
};

export type OwnedLookup = {
  /** Copies of the thing asked about. Empty means "Not in your library". */
  owned: OwnedCopy[];
  /** Copies of the same work in another format or on another platform. */
  softMatches: OwnedCopy[];
};

/** Ask by barcode text (any form), by release, or by work. A bare string is a code. */
export type OwnedQuery = string | { code?: string | null; releaseId?: string | null; workId?: string | null };

export interface OwnedIndex {
  /** Answers from the local copy, synchronously. */
  lookup(query: OwnedQuery): OwnedLookup;
  /** How many owned items the copy holds. */
  readonly size: number;
  /** Every entry, for saving. */
  entries(): SnapshotEntry[];
  /** A new index with these entries applied: deleted ones removed, the rest added or replaced. This one is unchanged. */
  apply(changes: readonly SnapshotEntry[]): OwnedIndex;
}

const EMPTY: OwnedLookup = { owned: [], softMatches: [] };

const toCopy = (e: SnapshotEntry): OwnedCopy => ({
  itemId: e.itemId,
  releaseId: e.releaseId,
  workId: e.workId,
  title: e.title,
  format: e.format,
  platform: e.platform,
  locationPath: e.locationPath,
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
    return this.items.size;
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
    // Asking by work alone: every copy of the work is a copy of what was asked about.
    if (workId && !releaseId && !key) take(this.byWork.get(workId));

    // Soft matches: other copies of the same work that are not what was asked about.
    const works = new Set<string>();
    if (workId) works.add(workId);
    for (const e of matched.values()) if (e.workId) works.add(e.workId);
    const soft = new Map<string, SnapshotEntry>();
    for (const work of works) for (const e of this.byWork.get(work) ?? []) if (!matched.has(e.itemId)) soft.set(e.itemId, e);

    if (matched.size === 0 && soft.size === 0) return EMPTY;
    return { owned: [...matched.values()].map(toCopy), softMatches: [...soft.values()].map(toCopy) };
  }
}

/** Builds an index from entries; deleted entries are ignored. */
export function createOwnedIndex(entries: readonly SnapshotEntry[] = []): OwnedIndex {
  return new MapOwnedIndex(new Map<string, SnapshotEntry>()).apply(entries);
}
