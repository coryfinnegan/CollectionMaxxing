// The last T0 auto-add of this session, so Undo can be offered on the scanner: the snackbar for 10 s
// after each add, and the counters bar's "Undo last add" until it is undone or something newer is added.
import { create } from 'zustand';

type AutoCommitUndoState = {
  /** The last auto-add: the session and candidate to take back, or null when there is nothing to undo. */
  last: { sessionId: string; candidateId: string } | null;
  setLast: (last: AutoCommitUndoState['last']) => void;
};

export const useAutoCommitUndoStore = create<AutoCommitUndoState>((set) => ({
  last: null,
  setLast: (last) => set({ last }),
}));
