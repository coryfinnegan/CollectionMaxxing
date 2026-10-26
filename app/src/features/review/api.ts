// Review's calls to the API, as plain functions over the generated client. Candidate answers are merged into the
// shared cards store, so the scanner and Review always show the same candidate.
import type {
  CandidateDto,
  CommitResultDto,
  DecideCandidateRequest,
  ScanSessionDto,
  UndoAutoCommitResultDto,
  UndoResultDto,
} from '@/api/generated/models';
import {
  commitScanSession,
  decideCandidate,
  scanBarcodes,
  searchForCandidate,
  undoAutoCommit as undoAutoCommitRequest,
  undoScanCommit,
} from '@/api/generated/scanning/scanning';
import type { StoreApi } from 'zustand/vanilla';
import { newIdempotencyKey } from '@/features/item/etag';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { dropCandidateLocally, flushDiscardOutbox, getDiscardOutbox, pruneDiscarded, withoutDiscarded } from '@/features/scan/session/discards';
import { loadSessionSnapshot } from '@/features/scan/session/sessionApi';
import { seedFromSnapshot } from '@/features/scan/session/sessionCounters';
import type { TrayStore } from '@/features/scan/tray/trayStore';
import { ApiError } from '@/lib/http';
import { commitBody, committable, type Draft } from './plan';

const keep = (candidate: CandidateDto) => useCardsStore.getState().merge([candidate], { announce: false });

export async function patchCandidate(sessionId: string, candidateId: string, body: DecideCandidateRequest): Promise<CandidateDto> {
  const response = await decideCandidate(sessionId, candidateId, body);
  if (response.status !== 200) throw new ApiError(response.status, {});
  keep(response.data);
  return response.data;
}

export async function searchCandidate(
  sessionId: string,
  candidateId: string,
  text: string,
  contextWorkIds?: string[],
): Promise<CandidateDto> {
  const response = await searchForCandidate(sessionId, candidateId, { text, contextWorkIds });
  if (response.status !== 200) throw new ApiError(response.status, {});
  keep(response.data);
  return response.data;
}

/** Skip: the server will not add it, and Review stops showing it. */
export async function skipCandidate(sessionId: string, candidateId: string): Promise<void> {
  const rejected = await patchCandidate(sessionId, candidateId, { decision: 'rejected' });
  useCardsStore.getState().remove(rejected.id);
}

/**
 * Discard: the rows leave Review at once — their cards, tray lines and snap items go with them — and the rejects
 * are queued so they land even when the app is killed first. Never throws: a discard that cannot reach the server
 * stays queued, and the rows stay gone. The counters reseed from the fresh snapshot, so the bar agrees at once.
 */
export async function discardCandidates({
  libraryId,
  sessionId,
  candidates,
  tray,
}: {
  libraryId: string;
  sessionId: string;
  candidates: readonly CandidateDto[];
  tray: StoreApi<TrayStore>;
}): Promise<void> {
  const queue = getDiscardOutbox();
  for (const c of candidates) {
    queue.enqueue({ libraryId, sessionId, candidateId: c.id });
    dropCandidateLocally({ tray, libraryId, candidate: c });
  }
  await flushDiscardOutbox({ libraryId });
  try {
    const snapshot = await loadSessionSnapshot(sessionId);
    pruneDiscarded(snapshot.candidates);
    seedFromSnapshot(sessionId, snapshot);
  } catch {
    // The rows are gone and the rejects are queued; the stream reseeds the counters when it can.
  }
}

/**
 * Takes back one T0 auto-add: the event's items go, and the candidate comes back with its evidence for the
 * person to decide. One Idempotency-Key per attempt: a retry after a lost answer gets the first answer back.
 */
export async function undoAutoCommit(
  sessionId: string,
  candidateId: string | undefined,
  key: string = newIdempotencyKey(),
): Promise<CandidateDto> {
  const response = await undoAutoCommitRequest(
    sessionId,
    candidateId === undefined ? null : { candidateId },
    { headers: { 'Idempotency-Key': key } },
  );
  if (response.status !== 200) throw new ApiError(response.status, {});
  const undone = (response.data as unknown as UndoAutoCommitResultDto).candidate;
  keep(undone);
  return undone;
}

/**
 * A bound capture: the stable read belongs to one Review row, which upgrades in place (same candidate id).
 * One Idempotency-Key per attempt: a retry after a lost answer gets the first answer back.
 */
export async function submitBoundBarcode(
  sessionId: string,
  candidateId: string,
  code: { value: string; symbology: string },
  key: string = newIdempotencyKey(),
): Promise<CandidateDto> {
  const response = await scanBarcodes(
    sessionId,
    { codes: [{ value: code.value, symbology: code.symbology }], candidateId },
    { headers: { 'Idempotency-Key': key } },
  );
  if (response.status !== 200) throw new ApiError(response.status, {});
  const [upgraded] = response.data.candidates;
  if (!upgraded) throw new ApiError(response.status, {});
  keep(upgraded);
  return upgraded;
}

/** What Review starts from: the session's own location, status and channel. */
export type SessionReviewContext = Pick<ScanSessionDto, 'defaultLocationId' | 'defaultStatus' | 'channel'>;

/** Loads the whole session into the cards store (a resumed scan, or Review opened first) and returns its context. */
export async function loadIntoCards(sessionId: string): Promise<SessionReviewContext> {
  const session = await loadSessionSnapshot(sessionId);
  pruneDiscarded(session.candidates);
  // Discarded and rejected candidates have no card: the snapshot confirms them, it never brings them back.
  useCardsStore.getState().merge(
    withoutDiscarded(session.candidates).filter((c) => c.decision !== 'rejected'),
    { announce: false },
  );
  return { defaultLocationId: session.defaultLocationId, defaultStatus: session.defaultStatus, channel: session.channel };
}

/**
 * Adds what is checked. The server only adds *accepted* candidates, so the decisions are made to match the
 * checkboxes first; then one commit (one transaction, one Idempotency-Key per attempt) that carries Add to, Status,
 * condition and the per-line overrides. Throws if the commit failed: nothing was added then.
 */
export async function commitDraft(
  sessionId: string,
  list: readonly CandidateDto[],
  draft: Draft,
  key: string,
): Promise<CommitResultDto> {
  const chosen = committable(list, draft);
  const chosenIds = new Set(chosen.map((c) => c.id));
  for (const c of chosen) {
    if (c.decision !== 'accepted') await patchCandidate(sessionId, c.id, { decision: 'accepted' });
  }
  // Something accepted earlier from a scanner card but unticked here must not be added.
  for (const c of list) {
    if (!chosenIds.has(c.id) && c.decision === 'accepted') await patchCandidate(sessionId, c.id, { decision: 'undecided' });
  }

  const response = await commitScanSession(sessionId, commitBody(draft, [...chosenIds]), { headers: { 'Idempotency-Key': key } });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data;
}

/**
 * Undo after "Added N items": the server removes the items that commit made for good (not into Recently deleted), reopens the
 * session and sends its results back with every decision, edit and copy count as they were. Then they are loaded into the cards
 * store, so Review shows them again. One Idempotency-Key per toast: a retry after a lost answer gets the first answer back.
 * Throws if it failed (a 409 means it is too late, or an item changed since): the items are left as they are then.
 */
export async function undoCommit(sessionId: string, key: string): Promise<UndoResultDto> {
  const response = await undoScanCommit(sessionId, { headers: { 'Idempotency-Key': key } });
  if (response.status !== 200) throw new ApiError(response.status, {});
  await loadIntoCards(sessionId);
  return response.data;
}
