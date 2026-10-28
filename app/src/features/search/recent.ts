// Recent searches, kept on the device (MMKV on the phone, memory on the web
// and in tests): most recent first, capped, a repeat moving to the front.
import { useCallback, useState } from 'react';
import { type KeyValueStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';

const STORAGE_KEY = 'search-recent-v1';
export const MAX_RECENT_SEARCHES = 8;

export function loadRecentSearches(storage: KeyValueStore): string[] {
  try {
    const parsed: unknown = JSON.parse(storage.getString(STORAGE_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    // A damaged blob is the same as no blob: recent searches rebuild themselves.
    return [];
  }
}

function store(storage: KeyValueStore, recent: string[]): string[] {
  storage.set(STORAGE_KEY, JSON.stringify(recent));
  return recent;
}

/** Saves the search up front, dropping a case-insensitive repeat and the oldest past the cap. Blank saves nothing. */
export function saveRecentSearch(storage: KeyValueStore, query: string): string[] {
  const term = query.trim();
  if (!term) return loadRecentSearches(storage);
  const rest = loadRecentSearches(storage).filter((saved) => saved.toLowerCase() !== term.toLowerCase());
  return store(storage, [term, ...rest].slice(0, MAX_RECENT_SEARCHES));
}

export function clearRecentSearches(storage: KeyValueStore): void {
  storage.remove(STORAGE_KEY);
}

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'search-recent' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let storage: KeyValueStore | undefined;

export const getRecentStorage = (): KeyValueStore => (storage ??= openStorage());

/** Tests swap in their own storage (and start clean). */
export function setRecentStorage(next: KeyValueStore | undefined) {
  storage = next;
}

export function useRecentSearches(): { recent: string[]; save(query: string): void; clear(): void } {
  const [recent, setRecent] = useState<string[]>(() => loadRecentSearches(getRecentStorage()));
  const save = useCallback((query: string) => setRecent(saveRecentSearch(getRecentStorage(), query)), []);
  const clear = useCallback(() => {
    clearRecentSearches(getRecentStorage());
    setRecent([]);
  }, []);
  return { recent, save, clear };
}
