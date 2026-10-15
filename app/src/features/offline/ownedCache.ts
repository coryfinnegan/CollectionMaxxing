// The saved copy of what each library owns: one blob per library in MMKV (memory on the web and in tests), holding the
// entries, the sync cursor and the time of the last complete sync. Nothing about another library is ever in the blob.
import { createOwnedIndex, type OwnedIndex, type SnapshotEntry } from './ownedIndex';

/** The slice of MMKV the cache needs (the same shape the scan tray uses). */
export interface OwnedStorage {
  getString(key: string): string | undefined;
  set(key: string, value: string): void;
  remove(key: string): unknown;
}

export class MemoryOwnedStorage implements OwnedStorage {
  private readonly values = new Map<string, string>();
  getString(key: string) {
    return this.values.get(key);
  }
  set(key: string, value: string) {
    this.values.set(key, value);
  }
  remove(key: string) {
    return this.values.delete(key);
  }
}

const VERSION = 1;
const blobKey = (libraryId: string) => `owned-index-v${VERSION}:${libraryId}`;
const LIBRARIES_KEY = `owned-index-v${VERSION}:libraries`;

/** Entries saved as short arrays: 5,000 items stay well under 1.5 MB. */
type Row = [itemId: string, releaseId: string | null, workId: string | null, title: string | null, format: string | null, platform: string | null, identifiers: string[], locationPath: string | null];
type Blob = { v: number; cursor: string | null; syncedAt: number | null; rows: Row[] };

const toRow = (e: SnapshotEntry): Row => [e.itemId, e.releaseId, e.workId, e.title, e.format, e.platform, e.identifiers, e.locationPath];
const fromRow = (r: Row): SnapshotEntry => ({
  itemId: r[0],
  deleted: false,
  releaseId: r[1],
  workId: r[2],
  title: r[3],
  format: r[4],
  platform: r[5],
  identifiers: r[6],
  locationPath: r[7],
});

export type OwnedState = {
  index: OwnedIndex;
  /** Where the next sync starts; null before the first sync. */
  cursor: string | null;
  /** When the last complete sync finished (ms since the epoch); null if one never has. */
  syncedAt: number | null;
};

const emptyState = (): OwnedState => ({ index: createOwnedIndex(), cursor: null, syncedAt: null });

/** One library's saved copy. Saving happens before the in-memory state changes, so a failed save changes nothing. */
export class OwnedCache {
  private current: OwnedState;
  private readonly listeners = new Set<() => void>();

  constructor(
    readonly libraryId: string,
    private readonly storage: OwnedStorage,
  ) {
    this.current = this.load();
  }

  get state(): OwnedState {
    return this.current;
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Saves, then swaps. Throws (leaving everything as it was) if saving fails. */
  commit(next: OwnedState) {
    const blob: Blob = { v: VERSION, cursor: next.cursor, syncedAt: next.syncedAt, rows: next.index.entries().map(toRow) };
    this.storage.set(blobKey(this.libraryId), JSON.stringify(blob));
    this.remember();
    this.current = next;
    this.listeners.forEach((l) => l());
  }

  /** Forgets this library's copy. */
  clear() {
    this.storage.remove(blobKey(this.libraryId));
    this.current = emptyState();
    this.listeners.forEach((l) => l());
  }

  private load(): OwnedState {
    try {
      const text = this.storage.getString(blobKey(this.libraryId));
      if (!text) return emptyState();
      const blob = JSON.parse(text) as Blob;
      if (blob.v !== VERSION || !Array.isArray(blob.rows)) return emptyState();
      return { index: createOwnedIndex(blob.rows.map(fromRow)), cursor: blob.cursor, syncedAt: blob.syncedAt };
    } catch {
      // A damaged blob is the same as no blob: the next sync pulls everything again.
      return emptyState();
    }
  }

  private remember() {
    const ids = readLibraries(this.storage);
    if (!ids.includes(this.libraryId)) this.storage.set(LIBRARIES_KEY, JSON.stringify([...ids, this.libraryId]));
  }
}

function readLibraries(storage: OwnedStorage): string[] {
  try {
    const parsed: unknown = JSON.parse(storage.getString(LIBRARIES_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

function openStorage(): OwnedStorage {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'owned-index' });
  } catch {
    return new MemoryOwnedStorage();
  }
}

let storage: OwnedStorage | undefined;
const caches = new Map<string, OwnedCache>();

/** The saved copy for one library. A user in two libraries has two, kept apart. */
export function getOwnedCache(libraryId: string): OwnedCache {
  let cache = caches.get(libraryId);
  if (!cache) {
    cache = new OwnedCache(libraryId, (storage ??= openStorage()));
    caches.set(libraryId, cache);
  }
  return cache;
}

/** Sign-out and unpair: forget every library's copy. */
export function clearOwnedData() {
  const store = (storage ??= openStorage());
  for (const id of new Set([...readLibraries(store), ...caches.keys()])) {
    store.remove(blobKey(id));
    caches.get(id)?.clear();
  }
  store.remove(LIBRARIES_KEY);
}

/** Tests swap in their own storage (and start clean). */
export function setOwnedStorage(next: OwnedStorage | undefined) {
  storage = next;
  caches.clear();
}
