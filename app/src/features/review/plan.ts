// What the owner has chosen on Review, and what that means for the commit. All pure.
// The commit endpoint takes no body, so location and status are applied to the new items afterwards with one
// bulk call per distinct choice (the contract has no per-candidate location or status).
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

export type BulkStep = { ids: string[]; patch: { locationId?: string | null; status?: ReviewStatus } };

/**
 * The bulk calls that turn new items (created with no location and the Owned status) into what the owner chose.
 * First Add to / Status for every new item, then a call per distinct override for the lines that differ.
 */
export function bulkSteps(draft: Draft, itemIds: readonly string[], createdByCandidate: Record<string, string | null | undefined>): BulkStep[] {
  const steps: BulkStep[] = [];
  const base: BulkStep['patch'] = {};
  if (draft.addTo) base.locationId = draft.addTo;
  if (draft.status !== 'owned') base.status = draft.status;
  if (itemIds.length > 0 && Object.keys(base).length > 0) steps.push({ ids: [...itemIds], patch: base });

  const groups = new Map<string, BulkStep>();
  for (const [candidateId, itemId] of Object.entries(createdByCandidate)) {
    const o = draft.overrides[candidateId];
    if (!itemId || !o) continue;
    const patch: BulkStep['patch'] = {};
    if (o.locationId !== undefined && o.locationId !== draft.addTo) patch.locationId = o.locationId;
    if (o.status !== undefined && o.status !== draft.status) patch.status = o.status;
    if (Object.keys(patch).length === 0) continue;
    const key = JSON.stringify(patch);
    const step = groups.get(key) ?? { ids: [], patch };
    step.ids.push(itemId);
    groups.set(key, step);
  }
  return [...steps, ...groups.values()];
}
