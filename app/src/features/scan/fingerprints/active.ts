// The store the scanner matches against right now: the signed-in user's, in the library on screen. The bridge sets it; the pipeline's
// `localMatch` hook reads it, so the hook needs no library argument. With none set (signed out, web) a match answers nothing.
import { createLocalMatch, type LocalMatch, type PrintVision } from './matcher';
import type { FingerprintStore } from './store';

let active: LocalMatch | undefined;

export function setActiveFingerprints(next: { store: FingerprintStore; vision: PrintVision; maxDistance?: () => number } | undefined) {
  active = next ? createLocalMatch(next) : undefined;
}

export const activeLocalMatch: LocalMatch = async (crop) => (active ? active(crop) : undefined);
