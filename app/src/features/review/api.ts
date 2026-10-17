// Review's calls to the API, as plain functions over the generated client. Candidate answers are merged into the
// shared cards store, so the scanner and Review always show the same candidate.
import type { CandidateDto, CommitResultDto, DecideCandidateRequest, UndoResultDto } from '@/api/generated/models';
import { commitScanSession, decideCandidate, searchForCandidate, undoScanCommit } from '@/api/generated/scanning/scanning';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { loadSession } from '@/features/scan/session/sessionApi';
import { ApiError } from '@/lib/http';
import { commitBody, committable, type Draft } from './plan';

const keep = (candidate: CandidateDto) => useCardsStore.getState().merge([candidate], { announce: false });

export async function patchCandidate(sessionId: string, candidateId: string, body: DecideCandidateRequest): Promise<CandidateDto> {
  const response = await decideCandidate(sessionId, candidateId, body);
  if (response.status !== 200) throw new ApiError(response.status, {});
  keep(response.data);
  return response.data;
}

export async function searchCandidate(sessionId: string, candidateId: string, text: string): Promise<CandidateDto> {
  const response = await searchForCandidate(sessionId, candidateId, { text });
  if (response.status !== 200) throw new ApiError(response.status, {});
  keep(response.data);
  return response.data;
}

/** Skip: the server will not add it, and Review stops showing it. */
export async function skipCandidate(sessionId: string, candidateId: string): Promise<void> {
  const rejected = await patchCandidate(sessionId, candidateId, { decision: 'rejected' });
  useCardsStore.getState().remove(rejected.id);
}

/** Loads the whole session into the cards store (a resumed scan, or Review opened first). */
export async function loadIntoCards(sessionId: string): Promise<void> {
  useCardsStore.getState().merge(await loadSession(sessionId), { announce: false });
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
