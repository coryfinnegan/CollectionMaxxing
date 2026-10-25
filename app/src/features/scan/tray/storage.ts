// The tray's storage: MMKV on the phone, memory when MMKV is not available (web, tests, Expo Go).
// Each library has its own tray (its lines and its session), kept apart the way the offline owned copy is:
// unflushed scans in one library can never be flushed or committed from another.
import { useCallback, useSyncExternalStore } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import { createTrayStore, MemoryKeyValueStore, type KeyValueStore, type TrayStore } from './trayStore';

const trayKey = (libraryId: string) => `scan-tray-v1:${libraryId}`;
const LIBRARIES_KEY = 'scan-tray-v1:libraries';
/** Before trays were keyed by library there was one global tray; the first library opened keeps it (once). */
const LEGACY_KEY = 'scan-tray-v1';

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'scan-tray' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

function readLibraries(storage: KeyValueStore): string[] {
  try {
    const parsed: unknown = JSON.parse(storage.getString(LIBRARIES_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

let storage: KeyValueStore | undefined;
let override: StoreApi<TrayStore> | undefined;
const stores = new Map<string, StoreApi<TrayStore>>();

/** The tray for one library. A user in two libraries has two, kept apart. */
export const getTrayStore = (libraryId: string): StoreApi<TrayStore> => {
  if (override) return override;
  let store = stores.get(libraryId);
  if (!store) {
    const backend = (storage ??= openStorage());
    adoptLegacy(backend, libraryId);
    store = createTrayStore({ storage: backend, storageKey: trayKey(libraryId) });
    stores.set(libraryId, store);
    const ids = readLibraries(backend);
    if (!ids.includes(libraryId)) backend.set(LIBRARIES_KEY, JSON.stringify([...ids, libraryId]));
  }
  return store;
};

function adoptLegacy(backend: KeyValueStore, libraryId: string) {
  try {
    if (!backend.getString(trayKey(libraryId)) && backend.getString(LEGACY_KEY)) {
      backend.set(trayKey(libraryId), backend.getString(LEGACY_KEY)!);
    }
    backend.remove(LEGACY_KEY);
  } catch {
    // Storage unavailable: this run's tray simply starts empty.
  }
}

/** Sign-out, unpair and account deletion: forget every library's tray. */
export function clearTrayData() {
  override?.getState().clear();
  const backend = (storage ??= openStorage());
  for (const id of new Set([...readLibraries(backend), ...stores.keys()])) {
    stores.get(id)?.getState().clear();
    backend.remove(trayKey(id));
  }
  backend.remove(LIBRARIES_KEY);
  backend.remove(LEGACY_KEY);
}

/**
 * Tests swap in one tray that stands in for every library's (tests cover one library at a time).
 * Multi-library tests use `setTrayStorage` instead. `undefined` restores the real keyed trays and starts clean.
 */
export const setTrayStore = (next: StoreApi<TrayStore> | undefined) => {
  override = next;
  if (!next) {
    storage = undefined;
    stores.clear();
  }
};

/** Tests that cover more than one library swap in their own storage (and start clean). */
export function setTrayStorage(next: KeyValueStore | undefined) {
  storage = next;
  stores.clear();
}

/** Reads one value from a tray that may not exist yet (no library): the fallback until it does. */
export function useTrayValue<T>(tray: StoreApi<TrayStore> | undefined, selector: (state: TrayStore) => T, fallback: T): T {
  const subscribe = useCallback((notify: () => void) => (tray ? tray.subscribe(notify) : () => {}), [tray]);
  return useSyncExternalStore(subscribe, () => (tray ? selector(tray.getState()) : fallback), () => fallback);
}
