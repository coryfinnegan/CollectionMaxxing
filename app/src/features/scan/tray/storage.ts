// The tray's storage: MMKV on the phone, memory when MMKV is not available (web, tests, Expo Go).
// `setTrayStore` lets tests swap in a store built on their own storage.
import type { StoreApi } from 'zustand/vanilla';
import { createTrayStore, MemoryKeyValueStore, type KeyValueStore, type TrayStore } from './trayStore';

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'scan-tray' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let store: StoreApi<TrayStore> | undefined;

export const getTrayStore = (): StoreApi<TrayStore> => (store ??= createTrayStore({ storage: openStorage() }));

export const setTrayStore = (next: StoreApi<TrayStore> | undefined) => {
  store = next;
};
