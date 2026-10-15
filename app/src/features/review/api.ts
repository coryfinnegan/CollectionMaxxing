// Review's calls to the API, as plain functions over the generated client. Candidate answers are merged into the
// shared cards store, so the scanner and Review always show the same candidate.
import { bulkItems, getItem, updateItem } from '@/api/generated/items/items';
import type { CandidateDto, CommitResultDto, DecideCandidateRequest } from '@/api/generated/models';
import { commitScanSession, decideCandidate, searchForCandidate } from '@/api/generated/scanning/scanning';
import { ifMatchHeader } from '@/features/item/etag';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { loadSession } from '@/features/scan/session/sessionApi';
import { ApiError } from '@/lib/http';
import { bulkSteps, committable, effective, type Draft } from './plan';

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
 * checkboxes first; then one commit (one transaction, one Idempotency-Key per attempt); then Add to, Status and
 * condition are applied to the new items. Throws if the commit itself failed: nothing was added then.
 */
export async function commitDraft(
  sessionId: string,
  libraryId: string,
  list: readonly CandidateDto[],
  draft: Draft,
  key: string,
): Promise<{ result: CommitResultDto; warning: string | null }> {
  const chosen = committable(list, draft);
  const chosenIds = new Set(chosen.map((c) => c.id));
  for (const c of chosen) {
    if (c.decision !== 'accepted') await patchCandidate(sessionId, c.id, { decision: 'accepted' });
  }
  // Something accepted earlier from a scanner card but unticked here must not be added.
  for (const c of list) {
    if (!chosenIds.has(c.id) && c.decision === 'accepted') await patchCandidate(sessionId, c.id, { decision: 'undecided' });
  }

  const response = await commitScanSession(sessionId, { headers: { 'Idempotency-Key': key } });
  if (response.status !== 200) throw new ApiError(response.status, {});
  const result = response.data;

  try {
    const after = await loadSession(sessionId);
    const created: Record<string, string | null> = {};
    for (const c of after) if (chosenIds.has(c.id)) created[c.id] = c.createdItemId;
    for (const step of bulkSteps(draft, result.itemIds, created)) {
      await bulkItems(libraryId, { ids: step.ids, action: 'patch', patch: step.patch });
    }
    for (const [candidateId, itemId] of Object.entries(created)) {
      const condition = effective(draft, candidateId).condition;
      if (!itemId || !condition) continue;
      const item = await getItem(libraryId, itemId);
      const etag = item.status === 200 ? item.headers.get('etag') : null;
      if (etag) await updateItem(libraryId, itemId, { condition }, { headers: ifMatchHeader(etag) });
    }
    return { result, warning: null };
  } catch {
    return { result, warning: "Couldn't set the location, status or condition on every item. Change them from each item." };
  }
}

/** Undo: the new items go to Recently deleted in one call (the contract has no batch undo, so this is the bulk soft delete). */
export async function undoCommit(libraryId: string, itemIds: readonly string[]): Promise<void> {
  const response = await bulkItems(libraryId, { ids: [...itemIds], action: 'delete' });
  if (response.status !== 200) throw new ApiError(response.status, {});
}

