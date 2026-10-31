// Cards whose item was moved to owned from a card (Got it, Found it), by candidate id, and what takes each move back. One store for
// the whole app, not state inside one hook: the strip over the camera and the result screen each run their own card actions, and
// both must show the same card ("In your library") and be able to take the same move back.
import { create } from 'zustand';
import type { MovedItem } from '@/features/item/moveStatus';

type MovedState = {
  byCandidate: Record<string, MovedItem>;
  set(candidateId: string, moved: MovedItem): void;
  clear(candidateId: string): void;
  reset(): void;
};

export const useMovedStore = create<MovedState>((set) => ({
  byCandidate: {},
  set: (candidateId, moved) => set((s) => ({ byCandidate: { ...s.byCandidate, [candidateId]: moved } })),
  clear: (candidateId) =>
    set((s) => {
      const { [candidateId]: _gone, ...rest } = s.byCandidate;
      return { byCandidate: rest };
    }),
  reset: () => set({ byCandidate: {} }),
}));
