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

/** The merge patch for the form. Blank text is sent as null, which clears the field. `today` is YYYY-MM-DD. */
export function buildPatch(form: ItemForm, original: ItemDto, today: string): UpdateItemRequest {
  const lent = form.status === 'lent';
  return {
    status: form.status,
    condition: form.condition,
    completeness: form.completeness,
    locationId: form.locationId,
    notes: orNull(form.notes),
    acquiredOn: orNull(form.acquiredOn),
    lentTo: lent ? orNull(form.lentTo) : null,
    lentOn: lent ? (original.status === 'lent' && original.lentOn ? original.lentOn : today) : null,
  };
}
