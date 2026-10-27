// The last T0 auto-add of this session, so Undo can be offered on the scanner: the snackbar for 10 s
// after each add (#451: the only Undo left on the scanner).
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
