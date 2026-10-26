// Review's "Scan barcode" binding (issue #339, design §8(c)): the row the next stable read belongs to.
// The scanner posts the read bound to this candidate, and the row upgrades in place. Held outside React so
// Review can set it while navigating and the scanner route can read it on mount.
import { createStore } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';

export type BoundScan = { sessionId: string; candidateId: string; title: string };

type BoundScanState = {
  binding: BoundScan | null;
  bind(binding: BoundScan): void;
  clear(): void;
};

export const boundScanStore = createStore<BoundScanState>()((set) => ({
  binding: null,
  bind: (binding) => set({ binding }),
  clear: () => set({ binding: null }),
}));

/** What the scanner's banner names: the release, the read text, the code, or a fallback. Never empty. */
export const boundTitleOf = (c: Pick<CandidateDto, 'release' | 'visibleText' | 'rawCode'>): string =>
  c.release?.title ?? c.visibleText ?? (c.rawCode ? `Code ${c.rawCode}` : 'Unknown item');
