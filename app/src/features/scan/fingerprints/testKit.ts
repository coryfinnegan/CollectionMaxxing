// Test doubles for the feature-print store: a vision module whose prints are numbers a test chooses, and stores on memory.
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import type { FeaturePrint } from '@/native/cm-vision';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { MemoryPrintStorage, setFingerprintStorage, getFingerprintStore, type StoredPrint } from './store';
import type { PrintVision } from './matcher';

export const LIB = MOCK_LIBRARY_ID;
export const REV = 1;

/** A print is `revision` plus a number; the distance between two prints is the difference of their numbers. */
export class FakeVision implements PrintVision {
  /** The print of each picture file, as a number; 0 when not set. */
  readonly values = new Map<string, number>();
  readonly revisions = new Map<string, number>();
  readonly printed: string[] = [];
  fail = false;

  set(uri: string, value: number, revision = REV) {
    this.values.set(uri, value);
    this.revisions.set(uri, revision);
    return this;
  }
  async featurePrint(uri: string): Promise<FeaturePrint> {
    this.printed.push(uri);
    if (this.fail) throw new Error('no print');
    return { revision: this.revisions.get(uri) ?? REV, data: String(this.values.get(uri) ?? 0) };
  }
  async featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number> {
    if (a.revision !== b.revision) throw new Error('prints of different revisions are never compared');
    return Math.abs(Number(a.data) - Number(b.data));
  }
}

export const printRow = (n: number, extra: Partial<StoredPrint> = {}): StoredPrint => ({
  itemId: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  releaseId: `00000000-0000-4000-9000-${String(n).padStart(12, '0')}`,
  revision: REV,
  print: String(n),
  createdAt: new Date(Date.UTC(2026, 9, 1, 0, 0, n)).toISOString(),
  synced: false,
  ...extra,
});

export function freshStore(libraryId = LIB, userId = 'user-1') {
  setFingerprintStorage(new MemoryPrintStorage());
  return getFingerprintStore({ userId, libraryId });
}

export const candidateOf = (id: string, extra: Partial<CandidateDto> = {}): CandidateDto =>
  ({
    id,
    seq: 1,
    source: 'picture',
    rawCode: null,
    symbology: null,
    visibleText: 'Dune',
    localId: `local-${id}`,
    box: null,
    boxSource: null,
    resolution: 'matched',
    score: 0.9,
    artDistance: null,
    needsArt: false,
    release: { id: `00000000-0000-4000-9000-${id.padStart(12, '0')}`, title: 'Dune' },
    inferred: null,
    alternatives: null,
    decision: 'undecided',
    copies: 1,
    alreadyOwnedCount: 0,
    ownedAt: null,
    createdItemId: null,
    changeSeq: 1,
    edits: null,
    userEdited: false,
    pendingReason: null,
    retryAfter: null,
    ...extra,
  }) as unknown as CandidateDto;
