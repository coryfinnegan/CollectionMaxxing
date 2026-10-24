// Client state (Zustand): which library the app is showing, remembered per device. The browser keeps it in localStorage; the
// iPhone keeps it in MMKV (already a dependency). If neither is available the choice lasts until the app closes.
import { create } from 'zustand';

const KEY = 'cm.activeLibraryId';

type Store = { getString(key: string): string | undefined; set(key: string, value: string): unknown; remove(key: string): unknown };

let phoneStore: Store | null | undefined;
function openPhoneStore(): Store | null {
  if (phoneStore !== undefined) return phoneStore;
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    phoneStore = createMMKV({ id: 'active-library' });
  } catch {
    phoneStore = null;
  }
  return phoneStore;
}

function readStored(): string | null {
  try {
    if (globalThis.localStorage) return globalThis.localStorage.getItem(KEY);
    return openPhoneStore()?.getString(KEY) ?? null;
  } catch {
    return null;
  }
}

function writeStored(id: string | null) {
  try {
    if (globalThis.localStorage) {
      if (id) globalThis.localStorage.setItem(KEY, id);
      else globalThis.localStorage.removeItem(KEY);
      return;
    }
    const store = openPhoneStore();
    if (id) store?.set(KEY, id);
    else store?.remove(KEY);
  } catch {
    // No storage: the choice lasts until the app closes.
  }
}

type LibraryState = {
  activeLibraryId: string | null;
  setActiveLibraryId: (id: string | null) => void;
};

export const useLibraryStore = create<LibraryState>((set) => ({
  activeLibraryId: readStored(),
  setActiveLibraryId: (id) => {
    writeStored(id);
    set({ activeLibraryId: id });
  },
}));
