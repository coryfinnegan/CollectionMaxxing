// The scan session's calls to the API, using the generated client. Everything here is plain functions so the
// hooks and the tests can share them.
import { createScanSession, decideCandidate, getScanSession, scanBarcodes } from '@/api/generated/scanning/scanning';
import { createItem } from '@/api/generated/items/items';
import type { CandidateDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { haptics } from '@/lib/haptics';
import { authHeaders, baseUrl } from '@/lib/auth/session';
import type { SseMessage } from '@/lib/sse';
import { connectSse } from '@/lib/sse';
import { useCardsStore, type CardEvent } from '../cards/cardsStore';
import type { FlushDeps } from '../tray/trayStore';

let chain: Promise<unknown> = Promise.resolve();

/** Plays result haptics one after another, so six cards arriving together do not buzz over each other. */
export function announce(events: { event: CardEvent }[]) {
  for (const { event } of events) {
    chain = chain.then(() => (event === 'owned' ? haptics.owned() : event === 'new' ? haptics.isNew() : haptics.notFound()));
  }
}

let creating: Promise<string> | undefined;

/** Makes the library's scan session. Barcodes and pictures can both ask at once; they share one request, so there is one session. */
export function createSessionOnce(libraryId: string): Promise<string> {
  creating ??= (async () => {
    const response = await createScanSession(libraryId, {});
    if (response.status !== 201) throw new ApiError(response.status, {});
    return response.data.id;
  })().finally(() => {
    creating = undefined;
  });
  return creating;
}

export function flushDeps(libraryId: string): FlushDeps {
  return {
    createSession: () => createSessionOnce(libraryId),
    async sendBatch(sessionId, batch) {
      const response = await scanBarcodes(
        sessionId,
        { codes: batch.entries.map((e) => ({ value: e.value, symbology: e.symbology, copies: e.copies })) },
        { headers: { 'Idempotency-Key': batch.key } },
      );
      if (response.status !== 200) throw new ApiError(response.status, {});
      announce(useCardsStore.getState().merge(response.data.candidates));
    },
    classify(error) {
      if (!(error instanceof ApiError)) return { offline: true }; // fetch itself failed: no connection
      if (error.status >= 500) return { offline: true };
      return { offline: false, message: friendlyError(error), resetSession: error.status === 404 };
    },
  };
}

/** The snapshot of a session, used to resume after the app was killed. */
export async function loadSession(sessionId: string, since?: number): Promise<CandidateDto[]> {
  const response = await getScanSession(sessionId, since === undefined ? undefined : { since });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data.candidates;
}

// The event cursor is the candidate's changeSeq (it goes up on every insert and update), not seq (the creation order).
const toMessage = (c: CandidateDto): SseMessage => ({ event: 'candidate.updated', id: String(c.changeSeq), data: JSON.stringify(c) });

/** Where to carry on from for a session that already has candidates: the newest change seen, or none for an empty one. */
export const resumeCursor = (candidates: CandidateDto[]): string | undefined => {
  const last = candidates.reduce((max, c) => Math.max(max, c.changeSeq), 0);
  return last > 0 ? String(last) : undefined;
};

/** Follows the session's events; falls back to polling `?since=` when the stream keeps failing. */
export function followSession(sessionId: string, lastEventId: string | undefined, tuning: { retryMs?: number; pollMs?: number } = {}) {
  return connectSse({
    url: `${baseUrl()}/api/v1/scan-sessions/${sessionId}/events`,
    headers: () => authHeaders('GET'),
    lastEventId,
    ...tuning,
    onMessage(message) {
      if (message.event !== 'candidate.added' && message.event !== 'candidate.updated') return;
      try {
        const candidate = JSON.parse(message.data) as Partial<CandidateDto>;
        announce(useCardsStore.getState().merge([candidate]));
      } catch {
        // a malformed event is skipped; the next one or the next poll fills the gap
      }
    },
    poll: async (last) => (await loadSession(sessionId, last === undefined ? 0 : Number(last))).map(toMessage),
  });
}

/** + Add: accept just this candidate. It is created when the batch is committed at Review. */
export async function acceptCandidate(sessionId: string, candidate: CandidateDto): Promise<void> {
  const response = await decideCandidate(sessionId, candidate.id, { decision: 'accepted' });
  if (response.status !== 200) throw new ApiError(response.status, {});
  useCardsStore.getState().merge([response.data]);
}

/** Wishlist: there is no per-candidate wishlist in the scan API, so this creates the wishlist item right away. */
export async function wishlistCandidate(libraryId: string, candidate: CandidateDto): Promise<void> {
  if (!candidate.release) return;
  const response = await createItem(
    libraryId,
    { releaseId: candidate.release.id, status: 'wishlist' },
    { headers: { 'Idempotency-Key': `wishlist-${candidate.id}` } },
  );
  if (response.status !== 201) throw new ApiError(response.status, {});
}

/** A removed scan must not be added at Review. A viewer cannot reject (403); that is fine, they cannot commit either. */
export async function rejectCandidate(sessionId: string, candidateId: string): Promise<void> {
  await decideCandidate(sessionId, candidateId, { decision: 'rejected' });
}
