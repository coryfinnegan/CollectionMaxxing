// Review's bound capture (design §8(b)(c), §11): the row the next read belongs to. A barcode binding takes the
// next stable read; a cover binding takes the next shutter picture as a cover read; an item binding takes the
// next stable read for a library item whose edition is still unverified. Either way the row upgrades in place.
// Held outside React so Review can set it while navigating and the scanner route can read it on mount.
import { createStore } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import { UNKNOWN_ITEM, unresolvedTitleOf } from './grouping';

export type BoundScan =
  | { sessionId: string; candidateId: string; title: string; kind: 'barcode' | 'cover' }
  | { libraryId: string; itemId: string; title: string; kind: 'itemBarcode' };

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

/** What the scanner's banner names: the release, the best unresolved guess, the code, or a fallback. Never empty, never raw read text (#435). */
export const boundTitleOf = (c: Pick<CandidateDto, 'release' | 'alternatives' | 'inferred' | 'rawCode'>): string =>
  c.release?.title ?? unresolvedTitleOf(c) ?? (c.rawCode ? `Code ${c.rawCode}` : UNKNOWN_ITEM);
