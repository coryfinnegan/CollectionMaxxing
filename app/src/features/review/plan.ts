// What the owner has chosen on Review, and what that means for the commit. All pure.
// Add to, Status, condition and per-line overrides ride along in the single commit request.
import type { CandidateDto, ItemCondition, ItemStatus, ReleaseDto } from '@/api/generated/models';
import { alternativesOf, canCommit, defaultChecked, groupOf, preselectedOf } from './grouping';

export type ReviewStatus = Extract<ItemStatus, 'owned' | 'wishlist'>;

export type LineOverride = {
  /** `undefined` follows Add to; `null` means "Not placed" for this line. */
  locationId?: string | null;
  status?: ReviewStatus;
  condition?: ItemCondition;
};

export type Draft = {
  /** Explicit taps. A row that was never tapped follows its default. */
  checked: Record<string, boolean>;
  /** Staged pick-one selections: candidate id to the chosen alternative's release id. The commit resolves them first. */
  picks: Record<string, string>;
  addTo: string | null;
  status: ReviewStatus;
  overrides: Record<string, LineOverride>;
};

export const emptyDraft = (): Draft => ({ checked: {}, picks: {}, addTo: null, status: 'owned', overrides: {} });

export const isChecked = (draft: Draft, c: CandidateDto): boolean => draft.checked[c.id] ?? defaultChecked(c, groupOf(c));

/**
 * The edition a pick-one row has selected: the person's staged tap first, the engine's
 * preselection next. A staged id the row no longer lists (a re-lookup swapped the
 * alternatives) selects nothing, and rows outside the pick group never select.
 */
export const selectedPick = (draft: Draft, c: CandidateDto): ReleaseDto | null => {
  if (groupOf(c) !== 'pick') return null;
  const options = alternativesOf(c);
  const id = draft.picks[c.id] ?? preselectedOf(c)?.releaseId ?? null;
  return options.find((r) => r.id === id) ?? null;
};

/** The pick-one rows with a selection: each resolves to one added item at commit. */
export const picked = (list: readonly CandidateDto[], draft: Draft): CandidateDto[] => list.filter((c) => selectedPick(draft, c));

/** The rows that will be added: checked, and decided enough to name a release. Never undecided, skipped or not-found rows. */
export const committable = (list: readonly CandidateDto[], draft: Draft): CandidateDto[] =>
  list.filter((c) => canCommit(c, groupOf(c)) && isChecked(draft, c));

/** The number on the button: checked rows plus staged pick-one selections. */
export const addCount = (list: readonly CandidateDto[], draft: Draft): number => committable(list, draft).length + picked(list, draft).length;

export const addLabel = (count: number) => (count === 1 ? 'Add 1 item' : `Add ${count} items`);

/** Above Add while rows are still being looked up: Add closes the scan, so they cannot come along. */
export const leftOutLine = (pending: number) =>
  pending === 1 ? '1 item is still looking up. Add leaves it out.' : `${pending} items are still looking up. Add leaves them out.`;

export function effective(draft: Draft, id: string): { locationId: string | null; status: ReviewStatus; condition: ItemCondition } {
  const o = draft.overrides[id] ?? {};
  return {
    locationId: o.locationId === undefined ? draft.addTo : o.locationId,
    status: o.status ?? draft.status,
    condition: o.condition ?? null,
  };
}

export type CommitBody = {
  addTo?: string;
  status?: ReviewStatus;
  overrides?: { candidateId: string; locationId?: string; status?: ReviewStatus; condition?: ItemCondition }[];
  /** The day on the phone (YYYY-MM-DD); `commitDraft` adds it, so the request's identity does not change at midnight. */
  acquiredOn?: string;
};

/**
 * The one commit request body. Add to and Status are for every item; a line only appears in `overrides` when it
 * differs. (The contract cannot say "Not placed" for a single line, so a null location override is left out.)
 */
export function commitBody(draft: Draft, candidateIds: readonly string[]): CommitBody {
  const body: CommitBody = {};
  if (draft.addTo) body.addTo = draft.addTo;
  if (draft.status !== 'owned') body.status = draft.status;
  const overrides: NonNullable<CommitBody['overrides']> = [];
  for (const id of candidateIds) {
    const o = draft.overrides[id];
    if (!o) continue;
    const line: NonNullable<CommitBody['overrides']>[number] = { candidateId: id };
    if (o.locationId && o.locationId !== draft.addTo) line.locationId = o.locationId;
    if (o.status !== undefined && o.status !== draft.status) line.status = o.status;
    if (o.condition) line.condition = o.condition;
    if (Object.keys(line).length > 1) overrides.push(line);
  }
  if (overrides.length > 0) body.overrides = overrides;
  return body;
}
