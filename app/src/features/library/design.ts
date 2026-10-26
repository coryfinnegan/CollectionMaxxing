// The library design choice (#357): Current, Castle, or Mall. Current is today's
// grid and stays the default; Castle shelves the collection in stone and Mall
// walks it as 90s stores. All three render the same items in the same order —
// the choice only changes the presentation. Kept on this device.
import { create } from 'zustand';

export type LibraryDesign = 'current' | 'castle' | 'mall';

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openDesignStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'library-design' });
  } catch {
    return memoryStore();
  }
}

const KEY = 'design';
let storage: StringStore | undefined;

export const getDesignStorage = (): StringStore => (storage ??= openDesignStorage());
export const setDesignStorage = (next: StringStore | undefined) => {
  storage = next;
};

const readChoice = (): LibraryDesign => {
  const stored = getDesignStorage().getString(KEY);
  return stored === 'castle' || stored === 'mall' ? stored : 'current';
};

type DesignState = { choice: LibraryDesign; set(choice: LibraryDesign): void; reload(): void };

/** Current until the person picks Castle or Mall in Settings → Appearance; kept on this device. */
export const useLibraryDesign = create<DesignState>((set) => ({
  choice: 'current',
  set: (choice) => {
    getDesignStorage().set(KEY, choice);
    set({ choice });
  },
  reload: () => set({ choice: readChoice() }),
}));
