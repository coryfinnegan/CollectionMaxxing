// Every picture file the scanner makes outside the snap store: the camera's capture, the scaled snap, a crop of one item, the
// two halves of a wide shot, a downloaded thumbnail. Each is registered when it is made and deleted as soon as it has done its
// job; whatever is still registered when the person discards, signs out or unpairs is deleted then (docs/02 "Privacy").
// The register is saved, so a file left by a killed app is still found and removed by the next clear.
import { MemoryKeyValueStore, type KeyValueStore } from './tray/trayStore';

const KEY = 'picture-files-v1';

/** Deletes one file. Resolves when it is gone, or was never there; rejects when it could not be deleted. */
export type PictureRemover = (uri: string) => Promise<void>;

/** Only the temp and cache folders, never the app's documents. */
const isTemp = (uri: string) => !/\/Documents\//i.test(uri) && /\/(tmp|Caches)\//i.test(uri);

const expoRemover: PictureRemover = async (uri) => {
  if (!isTemp(uri)) return; // nothing this register makes is outside the temp and cache folders
  let fs: typeof import('expo-file-system');
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    fs = require('expo-file-system') as typeof import('expo-file-system');
  } catch {
    return; // the web build and tests have no file system module
  }
  const file = new fs.File(uri);
  if (file.exists) file.delete();
};

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'picture-files' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

/** File address -> the snap it belongs to ('' for none yet). */
type Register = Record<string, string>;

let storage: KeyValueStore | undefined;
let remover: PictureRemover = expoRemover;
let register: Register | undefined;

const load = (): Register => {
  if (register) return register;
  try {
    const raw = (storage ??= openStorage()).getString(KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : {};
    register = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Register) : {};
  } catch {
    register = {};
  }
  return register;
};

const save = () => {
  try {
    (storage ??= openStorage()).set(KEY, JSON.stringify(load()));
  } catch {
    // storage full or unavailable: the files are still registered for this run
  }
};

/** Registers a picture file just made, optionally as one snap's. Returns the address so it can be used inline. */
export function trackPicture(uri: string, snapId = ''): string {
  load()[uri] = snapId;
  save();
  return uri;
}

/** Deletes one picture file now. It stays registered if it could not be deleted, so the next clear tries again. */
export async function discardPicture(uri: string): Promise<void> {
  try {
    await remover(uri);
  } catch {
    return;
  }
  if (uri in load()) {
    delete load()[uri];
    save();
  }
}

/** Deletes whatever one snap still has outside the snap store. */
export async function discardPicturesOf(snapId: string): Promise<void> {
  const own = Object.entries(load()).filter(([, owner]) => owner === snapId);
  await Promise.all(own.map(([uri]) => discardPicture(uri)));
}

/**
 * Discard of one library's scan: its snaps' files go, and whatever has no snap yet. A file with no snap (a capture still
 * being scaled, a half being read, a download) can only come from the scanner that is open, so it is this library's; other
 * libraries' snaps' files stay.
 */
export async function discardPicturesOfSnaps(snapIds: readonly string[]): Promise<void> {
  const doomed = new Set(snapIds);
  const own = Object.entries(load()).filter(([, owner]) => owner === '' || doomed.has(owner));
  await Promise.all(own.map(([uri]) => discardPicture(uri)));
}

/** Discard, sign-out and unpair: every registered file goes. */
export async function discardAllPictures(): Promise<void> {
  await Promise.all(Object.keys(load()).map((uri) => discardPicture(uri)));
}

/** For tests: the files a clear would delete. */
export const trackedPictures = (): string[] => Object.keys(load());

/** For tests: swap the storage and the file deleter, or put both back. */
export function setPictureFiles(next?: { storage?: KeyValueStore; remove?: PictureRemover }) {
  storage = next?.storage;
  remover = next?.remove ?? expoRemover;
  register = undefined;
}
