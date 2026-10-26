// The hoard's high-water mark (#363): the Great Hall's pile grows with the
// collection but never visually shrinks when items leave, so the peak per library
// is kept on this device.
import { useMemo } from 'react';

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openHoardStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'castle-hoard' });
  } catch {
    return memoryStore();
  }
}

let storage: StringStore | undefined;

export const getHoardStorage = (): StringStore => (storage ??= openHoardStorage());
export const setHoardStorage = (next: StringStore | undefined) => {
  storage = next;
};

const keyFor = (libraryId: string) => `hoard-peak:${libraryId}`;

/** The most items the library has held, raised when the count passes it, never lowered. */
export function hoardPeak(libraryId: string, total: number): number {
  const store = getHoardStorage();
  const stored = Number(store.getString(keyFor(libraryId)) ?? 0);
  const peak = Math.max(Number.isFinite(stored) ? stored : 0, Math.max(0, total));
  store.set(keyFor(libraryId), String(peak));
  return peak;
}

/** The high-water mark for this render: pure during render, persisted as a side effect of reading. */
export function useHoardPeak(libraryId: string, total: number): number {
  // The peak only moves upward, so memoizing on the inputs is safe across renders.
  return useMemo(() => hoardPeak(libraryId, total), [libraryId, total]);
}
