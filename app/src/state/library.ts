// Client state (Zustand): which library the app is showing. Native persistence comes later.
import { create } from 'zustand';

type LibraryState = {
  activeLibraryId: string | null;
  setActiveLibraryId: (id: string | null) => void;
};

export const useLibraryStore = create<LibraryState>((set) => ({
  activeLibraryId: null,
  setActiveLibraryId: (id) => set({ activeLibraryId: id }),
}));
