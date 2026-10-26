// Review's bound capture (design §8(b)(c)): the row the next read belongs to. A barcode binding takes the
// next stable read; a cover binding takes the next shutter picture as a cover read. Either way the row
// upgrades in place. Held outside React so Review can set it while navigating and the scanner route can
// read it on mount.
import { createStore } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';

export type BoundScan = { sessionId: string; candidateId: string; title: string; kind: 'barcode' | 'cover' };

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
