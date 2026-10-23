// Client state (Zustand): which library the app is showing, remembered per device. The browser keeps it in localStorage;
// the phone keeps it for the session until native storage is added (ONB-3).
import { create } from 'zustand';

const KEY = 'cm.activeLibraryId';

function readStored(): string | null {
  try {
    return globalThis.localStorage?.getItem(KEY) ?? null;
  } catch {
    return null;
  }
}

function writeStored(id: string | null) {
  try {
    if (id) globalThis.localStorage?.setItem(KEY, id);
    else globalThis.localStorage?.removeItem(KEY);
  } catch {
    // Private mode or no storage: the choice lasts until the page closes.
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
