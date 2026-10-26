// The scan session's calls to the API, using the generated client. Everything here is plain functions so the
// hooks and the tests can share them.
import { createScanSession, decideCandidate, getScanSession, scanBarcodes } from '@/api/generated/scanning/scanning';
import { createItem } from '@/api/generated/items/items';
import type { CandidateDto, CreateScanSessionRequest, ScanSessionDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { haptics } from '@/lib/haptics';
import { authHeaders, baseUrl } from '@/lib/auth/session';
import type { SseMessage } from '@/lib/sse';
import { connectSse } from '@/lib/sse';
import { useCardsStore, type CardEvent } from '../cards/cardsStore';
import type { FlushDeps } from '../tray/trayStore';
import { isDiscarded } from './discards';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { seedFromSnapshot, seedSessionCounters } from './sessionCounters';

let chain: Promise<unknown> = Promise.resolve();

/**
 * Plays result haptics one after another, so six cards arriving together do not buzz over each other.
 * An auto-add also becomes the session's "last add" for the Undo snackbar, when the session is known.
 */
export function announce(events: { id: string; event: CardEvent }[], sessionId?: string) {
  for (const { id, event } of events) {
    chain = chain.then(() =>
      event === 'owned'
        ? haptics.owned()
        : event === 'new'
          ? haptics.isNew()
          : event === 'added'
            ? haptics.succeed()
            : haptics.notFound(),
    );
    if (event === 'added' && sessionId) useAutoCommitUndoStore.getState().setLast({ sessionId, candidateId: id });
  }
}

const creating = new Map<string, Promise<string>>();

/**
 * Makes the library's scan session. Barcodes and pictures can both ask at once; they share one request per library,
 * so there is one session. Each library has its own in-flight request: Library B never receives Library A's session.
 * The context (location, status, media, channel) rides along so Review inherits it.
 */
export function createSessionOnce(libraryId: string, body: CreateScanSessionRequest = {}): Promise<string> {
  const running = creating.get(libraryId);
  if (running) return running;
  const next = (async () => {
    const response = await createScanSession(libraryId, body);
    if (response.status !== 201) throw new ApiError(response.status, {});
    return response.data.id;
  })().finally(() => {
    if (creating.get(libraryId) === next) creating.delete(libraryId);
  });
  creating.set(libraryId, next);
  return next;
}

export function flushDeps(libraryId: string, sessionBody: () => CreateScanSessionRequest = () => ({})): FlushDeps {
  return {
    createSession: () => createSessionOnce(libraryId, sessionBody()),
    async sendBatch(sessionId, batch) {
      const response = await scanBarcodes(
        sessionId,
        { codes: batch.entries.map((e) => ({ value: e.value, symbology: e.symbology, copies: e.copies })) },
        { headers: { 'Idempotency-Key': batch.key } },
      );
      if (response.status !== 200) throw new ApiError(response.status, {});
      announce(useCardsStore.getState().merge(response.data.candidates), sessionId);
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
  return (await loadSessionSnapshot(sessionId, since)).candidates;
}

/** The whole session: its candidates plus the context Review starts from (location, status, media, channel). */
export async function loadSessionSnapshot(sessionId: string, since?: number): Promise<ScanSessionDto> {
  const response = await getScanSession(sessionId, since === undefined ? undefined : { since });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data;
}

// The event cursor is the candidate's changeSeq (it goes up on every insert and update), not seq (the creation order).
const toMessage = (c: CandidateDto): SseMessage => ({ event: 'candidate.updated', id: String(c.changeSeq), data: JSON.stringify(c) });

/** Where to continue from for a session that already has candidates: the newest change seen, or none for an empty one. */
export const resumeCursor = (candidates: CandidateDto[]): string | undefined => {
  const last = candidates.reduce((max, c) => Math.max(max, c.changeSeq), 0);
  return last > 0 ? String(last) : undefined;
};

/** Routes one session event: candidates merge into the cards, `session.counters` seeds the counters bar. */
export function routeSessionMessage(sessionId: string, message: SseMessage) {
  if (message.event === 'session.counters') {
    try {
      const body = JSON.parse(message.data) as { counters?: unknown; changeSeq?: unknown };
      // The change counter rides in the body; the event id says the same when the body does not.
      const changeSeq = typeof body.changeSeq === 'number' ? body.changeSeq : message.id === undefined ? 0 : Number(message.id);
      seedSessionCounters(sessionId, { counters: body.counters, changeSeq });
    } catch {
      // a malformed event is skipped; the next one or the next poll fills the gap
    }
    return;
  }
  if (message.event !== 'candidate.added' && message.event !== 'candidate.updated' && message.event !== 'candidate.auto_committed')
    return;
  try {
    const candidate = JSON.parse(message.data) as Partial<CandidateDto>;
    // A discarded candidate never comes back: not while its reject is queued, not after it landed (a rejected
    // candidate has no card — Review filters it, and the scanner never drew one for it).
    if ((candidate.id && isDiscarded(candidate.id)) || candidate.decision === 'rejected') return;
    announce(useCardsStore.getState().merge([candidate]), sessionId);
  } catch {
    // a malformed event is skipped; the next one or the next poll fills the gap
  }
}

/** Follows the session's events; falls back to polling `?since=` when the stream keeps failing. */
export function followSession(sessionId: string, lastEventId: string | undefined, tuning: { retryMs?: number; pollMs?: number } = {}) {
  return connectSse({
    url: `${baseUrl()}/api/v1/scan-sessions/${sessionId}/events`,
    headers: () => authHeaders('GET'),
    lastEventId,
    ...tuning,
    onMessage: (message) => routeSessionMessage(sessionId, message),
    poll: async (last) => {
      // The snapshot carries the whole session's counters even when it carries only the changed candidates.
      const snapshot = await loadSessionSnapshot(sessionId, last === undefined ? 0 : Number(last));
      seedFromSnapshot(sessionId, snapshot);
      return snapshot.candidates.map(toMessage);
    },
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

