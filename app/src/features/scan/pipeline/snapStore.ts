// The on-phone snap store (docs/02 "Privacy"): for each picture the resized still, and for each item its box, its read
// text and a small crop. It lives in the app's temporary storage, is trimmed to a retention limit, is emptied by Discard,
// sign-out and unpair, and is never uploaded: CAM-3c (the photo beside the match) and CAM-3d (Import) read it from here.
import type { Rect } from '@/native/cm-vision';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';

/** A few days of snaps is plenty to open a result or fix an item; older pictures are not worth keeping. */
export const DEFAULT_RETENTION = { maxAgeMs: 7 * 24 * 60 * 60 * 1000, maxSnaps: 50 };

export interface StoredItem {
  localId: string;
  /** What was read from the picture for this item. */
  text: string;
  /** Normalized 0..1 on the still, origin top-left. */
  box?: Rect;
  /** The crop, as a file in the store. */
  cropUri?: string;
}

export interface StoredSnap {
  snapId: string;
  createdAt: number;
  stillUri: string;
  width: number;
  height: number;
  items: StoredItem[];
}

/** The files behind the store. The phone uses expo-file-system's cache folder; tests use memory. */
export interface SnapFiles {
  /** Copies a file into the store under `name` and returns where it is now. */
  put(name: string, fromUri: string): Promise<string>;
  /** Deletes these files; a missing one is fine. */
  remove(names: string[]): Promise<void>;
  /** Deletes every file in the store. */
  removeAll(): Promise<void>;
}

export class MemorySnapFiles implements SnapFiles {
  readonly files = new Map<string, string>();
  async put(name: string, fromUri: string) {
    this.files.set(name, fromUri);
    return `file:///snaps/${name}`;
  }
  async remove(names: string[]) {
    for (const name of names) this.files.delete(name);
  }
  async removeAll() {
    this.files.clear();
  }
}

/** expo-file-system's cache folder, loaded on first use. The OS may empty a cache folder; the store copes with that. */
export function expoSnapFiles(folder = 'cm-snaps'): SnapFiles {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const fs = require('expo-file-system') as typeof import('expo-file-system');
  const directory = () => {
    const dir = new fs.Directory(fs.Paths.cache, folder);
    if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
    return dir;
  };
  return {
    async put(name, fromUri) {
      const target = new fs.File(directory(), name);
      if (target.exists) target.delete();
      new fs.File(fromUri).copy(target);
      return target.uri;
    },
    async remove(names) {
      const dir = directory();
      for (const name of names) {
        const file = new fs.File(dir, name);
        if (file.exists) file.delete();
      }
    },
    async removeAll() {
      const dir = new fs.Directory(fs.Paths.cache, folder);
      if (dir.exists) dir.delete();
    },
  };
}

const INDEX_KEY = 'snap-store-v1';
const PINS_KEY = 'snap-store-pins-v1';

export interface SnapStoreDeps {
  files: SnapFiles;
  storage: KeyValueStore;
  now?: () => number;
  retention?: { maxAgeMs: number; maxSnaps: number };
}

const fileNames = (snap: StoredSnap) => [`${snap.snapId}-still.jpg`, ...snap.items.map((_, i) => `${snap.snapId}-${i}.jpg`)];

export class SnapStore {
  private snaps: StoredSnap[];
  private pins: Set<string>;
  private queue: Promise<unknown> = Promise.resolve();
  private readonly now: () => number;
  private readonly retention: { maxAgeMs: number; maxSnaps: number };

  constructor(private readonly deps: SnapStoreDeps) {
    this.now = deps.now ?? Date.now;
    this.retention = deps.retention ?? DEFAULT_RETENTION;
    this.snaps = this.load();
    this.pins = this.loadPins();
  }

  /** Work on the store one step at a time, so a wipe can never be overtaken by a save that started before it. */
  private run<T>(work: () => Promise<T>): Promise<T> {
    const next = this.queue.then(work, work);
    this.queue = next.catch(() => undefined);
    return next;
  }

  private load(): StoredSnap[] {
    try {
      const raw = this.deps.storage.getString(INDEX_KEY);
      const parsed: unknown = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed) ? (parsed as StoredSnap[]) : [];
    } catch {
      return []; // a damaged index is an empty store, never a crash
    }
  }

  private save() {
    try {
      this.deps.storage.set(INDEX_KEY, JSON.stringify(this.snaps));
    } catch {
      // storage full or unavailable: the files are still there for this run
    }
  }

  private loadPins(): Set<string> {
    try {
      const raw = this.deps.storage.getString(PINS_KEY);
      const parsed: unknown = raw ? JSON.parse(raw) : [];
      return new Set(Array.isArray(parsed) ? parsed.filter((p): p is string => typeof p === 'string') : []);
    } catch {
      return new Set();
    }
  }

  private savePins() {
    try {
      this.deps.storage.set(PINS_KEY, JSON.stringify([...this.pins]));
    } catch {
      // storage full or unavailable: the pins hold for this run
    }
  }

  /**
   * Pins a snap against retention trimming: the strip, Review and the pipeline need its still and crops while its
   * segments are unresolved. A pin survives the app being killed; forgetting the snap lifts it.
   */
  pin(snapId: string): void {
    if (this.pins.has(snapId)) return;
    this.pins.add(snapId);
    this.savePins();
  }

  /** Lifts a pin: the snap is resolved, committed or explicitly discarded, so retention may take it again. */
  unpin(snapId: string): void {
    if (!this.pins.delete(snapId)) return;
    this.savePins();
  }

  isPinned(snapId: string): boolean {
    return this.pins.has(snapId);
  }

  list(): StoredSnap[] {
    return this.snaps;
  }

  get(snapId: string): StoredSnap | undefined {
    return this.snaps.find((s) => s.snapId === snapId);
  }

  /** Keeps a copy of the resized still. If the copy fails the snap is still read; it just has no stored picture. */
  saveStill(snapId: string, photo: { uri: string; width: number; height: number }): Promise<StoredSnap | undefined> {
    return this.run(async () => {
      try {
        const stillUri = await this.deps.files.put(`${snapId}-still.jpg`, photo.uri);
        const snap: StoredSnap = { snapId, createdAt: this.now(), stillUri, width: photo.width, height: photo.height, items: [] };
        this.snaps = [...this.snaps.filter((s) => s.snapId !== snapId), snap];
        this.save();
        await this.trim();
        return snap;
      } catch {
        return undefined;
      }
    });
  }

  /** Records an item's box and read text, and keeps its crop. A missing crop is fine. */
  saveItem(snapId: string, item: { localId: string; text: string; box?: Rect }, cropFromUri?: string): Promise<StoredItem | undefined> {
    return this.run(async () => {
      const snap = this.get(snapId);
      if (!snap) return undefined;
      const existing = snap.items.findIndex((i) => i.localId === item.localId);
      const index = existing >= 0 ? existing : snap.items.length;
      let cropUri: string | undefined;
      if (cropFromUri) {
        try {
          cropUri = await this.deps.files.put(`${snapId}-${index}.jpg`, cropFromUri);
        } catch {
          cropUri = undefined;
        }
      }
      // A wipe may have happened while the crop was being copied.
      if (!this.get(snapId)) return undefined;
      const stored: StoredItem = { localId: item.localId, text: item.text, ...(item.box ? { box: item.box } : {}), ...(cropUri ? { cropUri } : {}) };
      const items = existing >= 0 ? snap.items.map((i, n) => (n === existing ? stored : i)) : [...snap.items, stored];
      this.snaps = this.snaps.map((s) => (s.snapId === snapId ? { ...s, items } : s));
      this.save();
      return stored;
    });
  }

  /** Forgets one snap and its files. */
  forget(snapId: string): Promise<void> {
    return this.run(async () => {
      const snap = this.get(snapId);
      if (!snap) {
        this.unpin(snapId); // a pin without a snap is nothing to keep
        return;
      }
      this.snaps = this.snaps.filter((s) => s.snapId !== snapId);
      this.save();
      this.unpin(snapId);
      await this.deps.files.remove(fileNames(snap)).catch(() => undefined);
    });
  }

  /** Drops snaps older than the retention age, then the oldest beyond the retention count. Pinned snaps stay. */
  prune(): Promise<void> {
    return this.run(() => this.trim());
  }

  private async trim() {
    const cutoff = this.now() - this.retention.maxAgeMs;
    const fresh = this.snaps.filter((s) => s.createdAt >= cutoff);
    const keep = [...fresh].sort((a, b) => b.createdAt - a.createdAt).slice(0, this.retention.maxSnaps);
    const kept = new Set(keep.map((s) => s.snapId));
    const dropped = this.snaps.filter((s) => !kept.has(s.snapId) && !this.pins.has(s.snapId));
    if (dropped.length === 0) return;
    this.snaps = this.snaps.filter((s) => kept.has(s.snapId) || this.pins.has(s.snapId));
    this.save();
    await this.deps.files.remove(dropped.flatMap(fileNames)).catch(() => undefined);
  }

  /** Discard, sign-out and unpair: no still, crop or box survives. */
  clear(): Promise<void> {
    return this.run(async () => {
      this.snaps = [];
      this.pins = new Set();
      try {
        this.deps.storage.remove(INDEX_KEY);
        this.deps.storage.remove(PINS_KEY);
      } catch {
        // nothing more can be done about a storage that will not forget
      }
      await this.deps.files.removeAll().catch(() => undefined);
    });
  }
}

function openIndexStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'snap-store' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

function openFiles(): SnapFiles {
  try {
    return expoSnapFiles();
  } catch {
    return new MemorySnapFiles(); // the web build and tests have no file system module
  }
}

let current: SnapStore | undefined;

/** The snap store for this app run, exported for CAM-3c and CAM-3d. */
export const getSnapStore = (): SnapStore => (current ??= new SnapStore({ files: openFiles(), storage: openIndexStorage() }));

export const setSnapStore = (next: SnapStore | undefined) => {
  current = next;
};
