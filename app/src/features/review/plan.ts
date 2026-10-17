// What the owner has chosen on Review, and what that means for the commit. All pure.
// Add to, Status, condition and per-line overrides ride along in the single commit request.
import type { CandidateDto, ItemCondition, ItemStatus } from '@/api/generated/models';
import { canCommit, defaultChecked, groupOf } from './grouping';

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
  addTo: string | null;
  status: ReviewStatus;
  overrides: Record<string, LineOverride>;
};

export const emptyDraft = (): Draft => ({ checked: {}, addTo: null, status: 'owned', overrides: {} });

export const isChecked = (draft: Draft, c: CandidateDto): boolean => draft.checked[c.id] ?? defaultChecked(c, groupOf(c));

/** The rows that will be added: checked, and decided enough to name a release. Never undecided, skipped or not-found rows. */
export const committable = (list: readonly CandidateDto[], draft: Draft): CandidateDto[] =>
  list.filter((c) => canCommit(c, groupOf(c)) && isChecked(draft, c));

/** The number on the button. */
export const addCount = (list: readonly CandidateDto[], draft: Draft): number => committable(list, draft).length;

export const addLabel = (count: number) => (count === 1 ? 'Add 1 item' : `Add ${count} items`);

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
