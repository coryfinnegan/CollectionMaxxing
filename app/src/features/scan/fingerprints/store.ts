// The on-phone feature-print store (docs/02 "Learning loop"): for each item the person has confirmed, the Apple feature print of
// its picture, so a new snap of the same cart or sleeve matches at once, offline. One blob per signed-in user and library in MMKV
// (memory on the web and in tests). A print is a vector, not a picture: no image file is kept here. Sign-out and unpair wipe it.
import type { KeyValueStore } from '@/features/scan/tray/trayStore';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';

export interface StoredPrint {
  itemId: string;
  releaseId: string;
  /** The vision module's print format version. Prints of different revisions are never compared. */
  revision: number;
  /** The opaque print, base64. */
  print: string;
  /** ISO time. */
  createdAt: string;
  /** True once the PC has it (backed up there, or restored from there). */
  synced: boolean;
}

export interface PrintScope {
  userId?: string | null;
  libraryId: string;
}

export interface PrintStorage extends KeyValueStore {
  clearAll(): void;
}

export class MemoryPrintStorage extends MemoryKeyValueStore implements PrintStorage {
  private readonly keys = new Set<string>();
  override set(key: string, value: string) {
    this.keys.add(key);
    super.set(key, value);
  }
  clearAll() {
    for (const key of this.keys) super.remove(key);
    this.keys.clear();
  }
}

const VERSION = 1;
const keyOf = (scope: PrintScope) => `fingerprints-v${VERSION}:${scope.userId ?? 'user'}:${scope.libraryId}`;

type Blob = { v: number; restoredThrough: string | null; deviceRevision?: number | null; rows: StoredPrint[] };

const isRow = (r: unknown): r is StoredPrint => {
  const x = r as Partial<StoredPrint> | null;
  return (
    !!x &&
    typeof x.itemId === 'string' &&
    typeof x.releaseId === 'string' &&
    typeof x.revision === 'number' &&
    typeof x.print === 'string' &&
    typeof x.createdAt === 'string' &&
    typeof x.synced === 'boolean'
  );
};

export class FingerprintStore {
  private rows = new Map<string, StoredPrint>();
  private through: string | null = null;
  private revision: number | null = null;
  private cleared = false;
  private readonly listeners = new Set<() => void>();

  constructor(
    readonly scope: PrintScope,
    private readonly storage: PrintStorage,
  ) {
    this.load();
  }

  private load() {
    try {
      const raw = this.storage.getString(keyOf(this.scope));
      const blob = raw ? (JSON.parse(raw) as Blob) : undefined;
      if (!blob || blob.v !== VERSION || !Array.isArray(blob.rows)) return;
      this.rows = new Map(blob.rows.filter(isRow).map((r) => [r.itemId, r]));
      this.through = typeof blob.restoredThrough === 'string' ? blob.restoredThrough : null;
      this.revision = typeof blob.deviceRevision === 'number' ? blob.deviceRevision : null;
    } catch {
      // A damaged blob is an empty store; the next restore pulls everything again.
    }
  }

  private save() {
    if (this.cleared) return; // a restore that was still running when the person signed out must not bring prints back
    try {
      const blob: Blob = { v: VERSION, restoredThrough: this.through, deviceRevision: this.revision, rows: [...this.rows.values()] };
      this.storage.set(keyOf(this.scope), JSON.stringify(blob));
    } catch {
      // storage full or unavailable: the prints still work for this run
    }
    this.listeners.forEach((l) => l());
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  all(): StoredPrint[] {
    return [...this.rows.values()];
  }

  get(itemId: string): StoredPrint | undefined {
    return this.rows.get(itemId);
  }

  get size() {
    return this.rows.size;
  }

  unsynced(): StoredPrint[] {
    return this.all().filter((r) => !r.synced);
  }

  /** Saves the print for an item; a new one replaces the old. */
  put(row: StoredPrint) {
    this.rows.set(row.itemId, row);
    this.save();
  }

  markSynced(itemIds: readonly string[]) {
    for (const id of itemIds) {
      const row = this.rows.get(id);
      if (row) this.rows.set(id, { ...row, synced: true });
    }
    this.save();
  }

  /** Prints restored from the PC. A print this phone has not backed up yet is never overwritten. */
  merge(restored: readonly StoredPrint[]) {
    for (const row of restored) {
      const mine = this.rows.get(row.itemId);
      if (mine && !mine.synced) continue;
      this.rows.set(row.itemId, { ...row, synced: true });
    }
    this.save();
  }

  remove(itemIds: readonly string[]) {
    let changed = false;
    for (const id of itemIds) changed = this.rows.delete(id) || changed;
    if (changed) this.save();
  }

  /** Where the next restore starts (the newest `createdAt` it has seen); null before the first restore. */
  get restoredThrough() {
    return this.through;
  }

  setRestoredThrough(value: string | null) {
    this.through = value;
    this.save();
  }

  /** The print revision this phone produces, once it has made a print (null before). Other revisions are kept but never compared. */
  get deviceRevision() {
    return this.revision;
  }

  setDeviceRevision(value: number) {
    if (this.revision === value) return;
    this.revision = value;
    this.save();
  }

  /** Forgets this scope's prints. */
  clear() {
    this.cleared = true;
    this.rows.clear();
    this.through = null;
    this.revision = null;
    try {
      this.storage.remove(keyOf(this.scope));
    } catch {
      // nothing more can be done about a storage that will not forget
    }
    this.listeners.forEach((l) => l());
  }
}

function openStorage(): PrintStorage {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'feature-prints' }) as unknown as PrintStorage;
  } catch {
    return new MemoryPrintStorage();
  }
}

let storage: PrintStorage | undefined;
const stores = new Map<string, FingerprintStore>();

/** The store for one user and library. Two libraries have two, kept apart. */
export function getFingerprintStore(scope: PrintScope): FingerprintStore {
  const key = keyOf(scope);
  let store = stores.get(key);
  if (!store) {
    store = new FingerprintStore(scope, (storage ??= openStorage()));
    stores.set(key, store);
  }
  return store;
}

/** Sign-out, unpair and account deletion: no print of any user or library survives. */
export async function clearFingerprintData(): Promise<void> {
  (storage ??= openStorage()).clearAll();
  for (const store of stores.values()) store.clear();
  stores.clear();
}

/** Tests swap in their own storage (and start clean). */
export function setFingerprintStorage(next?: PrintStorage) {
  storage = next;
  stores.clear();
}
