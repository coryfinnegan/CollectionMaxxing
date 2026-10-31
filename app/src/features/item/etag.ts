// Pure logic for items: ETag / If-Match, idempotency keys, and turning the edit form into a PATCH body.
import type { ItemCompleteness, ItemCondition, ItemDto, ItemStatus, UpdateItemRequest } from '@/api/generated/models';

/** The If-Match header for a PATCH or DELETE, from the ETag the item was loaded with. */
export function ifMatchHeader(etag: string): { 'If-Match': string } {
  const quoted = etag.startsWith('"') || etag.startsWith('W/') ? etag : `"${etag}"`;
  return { 'If-Match': quoted };
}

/** A fresh Idempotency-Key for one attempt to create an item. */
export function newIdempotencyKey(): string {
  const part = () => Math.random().toString(16).slice(2, 10).padEnd(8, '0');
  return `${Date.now().toString(16)}-${part()}-${part()}`;
}

/** Empty, or a real calendar date as YYYY-MM-DD. */
export function isValidDate(value: string): boolean {
  if (!value) return true;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().slice(0, 10) === value;
}

export type ItemForm = {
  status: ItemStatus;
  condition: ItemCondition;
  completeness: ItemCompleteness;
  locationId: string | null;
  notes: string;
  acquiredOn: string;
  lentTo: string;
};

export const formFromItem = (item: ItemDto): ItemForm => ({
  status: item.status,
  condition: item.condition,
  completeness: item.completeness,
  locationId: item.locationId,
  notes: item.notes ?? '',
  acquiredOn: item.acquiredOn ?? '',
  lentTo: item.lentTo ?? '',
});

const orNull = (text: string): string | null => (text.trim() ? text.trim() : null);

/**
 * The merge patch for the form: only what the person changed from `original`, the version the form opened with.
 * Fields they left alone stay out, so saving can never write an old value over someone else's newer one.
 * Blank text is sent as null, which clears the field. `today` is YYYY-MM-DD. An untouched form gives `{}`.
 */
export function buildPatch(form: ItemForm, original: ItemDto, today: string): UpdateItemRequest {
  const was = formFromItem(original);
  const patch: UpdateItemRequest = {};
  if (form.status !== was.status) patch.status = form.status;
  if (form.condition !== was.condition) patch.condition = form.condition;
  if (form.completeness !== was.completeness) patch.completeness = form.completeness;
  if (form.locationId !== was.locationId) patch.locationId = form.locationId;
  if (orNull(form.notes) !== orNull(was.notes)) patch.notes = orNull(form.notes);
  if (orNull(form.acquiredOn) !== orNull(was.acquiredOn)) patch.acquiredOn = orNull(form.acquiredOn);

  if (form.status === 'lent') {
    if (original.status !== 'lent') {
      // Newly lent: say to whom and from when.
      patch.lentTo = orNull(form.lentTo);
      patch.lentOn = today;
    } else if (orNull(form.lentTo) !== orNull(was.lentTo)) {
      patch.lentTo = orNull(form.lentTo);
    }
  } else if (original.status === 'lent') {
    // No longer lent: the borrower and the date go with it.
    patch.lentTo = null;
    patch.lentOn = null;
  }
  return patch;
}

/**
 * Whether saving would lend the item out, or change who has it, with no name: the server refuses that. A lend that was saved without a
 * name earlier and is left as it is is not flagged, so the rest of that item can still be edited.
 */
export function lentToMissing(form: ItemForm, original: ItemDto): boolean {
  if (form.status !== 'lent' || orNull(form.lentTo) !== null) return false;
  return original.status !== 'lent' || orNull(formFromItem(original).lentTo) !== null;
}

/** Whether the patch changes anything at all. */
export const isEmptyPatch = (patch: UpdateItemRequest): boolean => Object.keys(patch).length === 0;
