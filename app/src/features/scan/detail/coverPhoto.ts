// "Use my photo" (docs/02 "Privacy", docs/06 M4): sends the crop of ONE item, the small JPEG the snap store already holds (no new
// file is made, so there is nothing extra to track or delete), and only when an editor taps the button. The server keeps it as that
// item's cover. Opening, swiping, highlighting and Fix it never reach this file. Two other paths also send a picture, and the privacy
// note (about/privacyNote.ts) lists all three: a close call's check (coverVerify.ts, discarded) and the crop of a row kept as
// unverified (review/provisionals.ts, kept until the row is deleted or linked to an item).
import { create } from 'zustand';
import { uploadCandidatePhoto } from '@/api/generated/scanning/scanning';
import { useCardsStore } from '../cards/cardsStore';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';

/** The server takes up to 4 MB; check here so a too-big file is never sent. */
export const MAX_COVER_BYTES = 4 * 1024 * 1024;

/** Reads a stored crop as a file to send. The phone reads the file address; tests swap it. */
export type CropReader = (uri: string) => Promise<Blob>;

const fileReader: CropReader = async (uri) => (await fetch(uri)).blob();

let reader: CropReader = fileReader;

/** For tests: swap the crop reader, or put it back. */
export const setCropReader = (next?: CropReader) => {
  reader = next ?? fileReader;
};

/** Which cards the person has sent a photo for, and the crop they sent, so the cover choice shows it. */
export const useCoverStore = create<{ sent: Record<string, string>; mark(candidateId: string, cropUri: string): void; reset(): void }>((set, get) => ({
  sent: {},
  mark: (candidateId, cropUri) => set({ sent: { ...get().sent, [candidateId]: cropUri } }),
  reset: () => set({ sent: {} }),
}));

export type CoverResult = { ok: true } | { ok: false; reason: 'blocked' | 'nothing' | 'busy' | 'failed'; message?: string };

const inFlight = new Set<string>();

export const TOO_BIG = 'That picture is too big to use. Try snapping it again, a little closer.';

/** Sends the crop of one item as its cover. A viewer cannot: nothing is read and nothing is sent. */
export async function uploadCover(args: { canEdit: boolean; sessionId: string | null; candidateId: string; cropUri: string | undefined }): Promise<CoverResult> {
  const { canEdit, sessionId, candidateId, cropUri } = args;
  if (!canEdit) return { ok: false, reason: 'blocked' };
  if (!sessionId || !cropUri) return { ok: false, reason: 'nothing' };
  if (inFlight.has(candidateId)) return { ok: false, reason: 'busy' };
  inFlight.add(candidateId);
  try {
    const crop = await reader(cropUri);
    if (crop.size > MAX_COVER_BYTES) return { ok: false, reason: 'failed', message: TOO_BIG };
    const response = await uploadCandidatePhoto(sessionId, candidateId, { file: crop });
    if (response.status !== 200) throw new ApiError(response.status, {});
    useCardsStore.getState().merge([response.data], { announce: false });
    useCoverStore.getState().mark(candidateId, cropUri);
    return { ok: true };
  } catch (e) {
    const message = e instanceof ApiError && e.status === 413 ? TOO_BIG : friendlyError(e, "That didn't work. Your photo is still on your phone. Try again.");
    return { ok: false, reason: 'failed', message };
  } finally {
    inFlight.delete(candidateId);
  }
}
