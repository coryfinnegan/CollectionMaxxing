// E2E-only seed (#432): a review session with one row per state, reached as `/review?seed=review-rows`.
// start.web.ts loads this only when EXPO_PUBLIC_E2E=1, so production exports drop it with the branch.
import type { CandidateDto } from '@/api/generated/models';
import { candidate, release } from '@/features/review/testData';
import { getPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { getTrayStore } from '@/features/scan/tray/storage';
import { MOCK_LIBRARY_ID } from './data';
import { scanMock } from './scan';

export const REVIEW_ROWS_SESSION = 'e2e00000-0000-4000-8000-000000000001';

/**
 * One inline thumbnail per row (CSP allows data: images): a flat tile with the row's initial. Base64, because the
 * DOM layer re-encodes the escapes of a plain SVG data URI and the image fails to load.
 */
const thumb = (art: string): string => `data:image/svg+xml;base64,${art}`;
const ART: Record<string, string> = {
  A: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjYjM1NDFlIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5BPC90ZXh0Pjwvc3ZnPg==',
  D1: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjNWI1YmQ2Ii8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5EPC90ZXh0Pjwvc3ZnPg==',
  D2: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjMmU3ZDRmIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5EPC90ZXh0Pjwvc3ZnPg==',
  M: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjYTMyZTJlIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5NPC90ZXh0Pjwvc3ZnPg==',
  S: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjNmI2YjZiIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5TPC90ZXh0Pjwvc3ZnPg==',
  Y1: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjN2M0YTFlIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5TPC90ZXh0Pjwvc3ZnPg==',
  Y2: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjM2U2YjRmIi8+PHRleHQgeD0iMjQiIHk9IjMxIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5TPC90ZXh0Pjwvc3ZnPg==',
};

// The seeded pick-one row (#440): two editions of one title in the wire shape the API sends,
// one with catalog art and one falling back to the captured crop, the 2003 edition preselected.
const suddenly = (['Y1', null] as const).map((art, i) => ({
  releaseId: `e2e00000-0000-4000-8000-00000000002${i + 1}`,
  title: 'Suddenly You',
  format: 'other',
  platform: null,
  year: 2001 + i * 2,
  score: 0.9 - i * 0.1,
  creators: ['Lisa Kleypas'],
  coverUrl: art ? thumb(ART[art]!) : null,
  alreadyOwnedCount: 0,
  artworkUrl: null,
}));

/** One row per Review state: pick, not found, owned, ready, and two still looking up (quota + identifying). */
function reviewRows(): CandidateDto[] {
  return [
    {
      ...candidate({
        id: 'e2e00000-0000-4000-8000-000000000011',
        seq: 1,
        changeSeq: 1,
        source: 'picture_text',
        visibleText: 'Suddenly You',
        localId: 'seed-pick',
        resolution: 'ambiguous',
        release: release(11, 'Suddenly You', { year: 2001, format: 'other' }),
        alternatives: suddenly as never,
        guidanceCode: 'pick_match',
      }),
      suggestedReleaseId: suddenly[1]!.releaseId,
      suggestionReason: 'Cover match',
    } as CandidateDto,
    candidate({
      id: 'e2e00000-0000-4000-8000-000000000012',
      seq: 2,
      changeSeq: 2,
      source: 'picture_text',
      visibleText: 'Dune Mess',
      localId: 'seed-lost',
      resolution: 'not_found',
      release: null,
    }),
    candidate({
      id: 'e2e00000-0000-4000-8000-000000000013',
      seq: 3,
      changeSeq: 3,
      source: 'picture_text',
      visibleText: 'Dune',
      localId: 'seed-owned',
      resolution: 'matched',
      release: release(1, 'Dune', { format: 'paperback', year: 1990 }),
      alreadyOwnedCount: 1,
      ownedAt: ['Living room › Shelf 2'],
    }),
    // The barcode row (#452): resolved at scan time with its match preselected, waiting for the commit.
    candidate({
      id: 'e2e00000-0000-4000-8000-000000000014',
      seq: 4,
      changeSeq: 4,
      source: 'barcode',
      shotKind: 'barcode',
      rawCode: '045496730647',
      resolution: 'matched',
      tier: 'T0',
      decision: 'accepted',
      release: release(2, 'Super Mario 64', { format: 'game_cartridge' }),
    }),
    candidate({
      id: 'e2e00000-0000-4000-8000-000000000015',
      seq: 5,
      changeSeq: 5,
      resolution: 'pending',
      release: null,
      rawCode: '111',
      pendingReason: 'quota',
    }),
    candidate({
      id: 'e2e00000-0000-4000-8000-000000000016',
      seq: 6,
      changeSeq: 6,
      source: 'picture_text',
      visibleText: 'Spine Row',
      localId: 'seed-waiting',
      resolution: 'pending',
      release: null,
      pendingReason: 'lookup',
    }),
  ];
}

/** The phone's own crops for the picture rows, paired by local id. */
function seedSnaps(): void {
  const thumbs: Record<string, string> = {
    'seed-pick': thumb(ART.Y2!),
    'seed-lost': thumb(ART.D1!),
    'seed-owned': thumb(ART.D2!),
    'seed-waiting': thumb(ART.S!),
  };
  getPipelineStore().getState().addSnap({
    snapId: 'seed-snap-1',
    libraryId: MOCK_LIBRARY_ID,
    trigger: 'shutter',
    createdAt: 1,
    thumbUri: thumbs['seed-pick']!,
    phase: 'read',
    sent: true,
    sessionId: REVIEW_ROWS_SESSION,
    items: Object.entries(thumbs).map(([localId, thumbUri]) => ({ localId, text: localId, title: localId, payload: { localId, text: localId }, thumbUri })),
    unreadableCount: 0,
  });
}

export function seedReviewRows(): void {
  scanMock.sessions.set(REVIEW_ROWS_SESSION, {
    id: REVIEW_ROWS_SESSION,
    libraryId: MOCK_LIBRARY_ID,
    seq: 100,
    candidates: reviewRows(),
    answers: new Map(),
    status: 'open',
    provisionals: [],
  });
  getTrayStore(MOCK_LIBRARY_ID).getState().setSessionId(REVIEW_ROWS_SESSION);
  seedSnaps();
}
