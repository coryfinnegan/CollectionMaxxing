// The PATCH body for changing one item's status from the table. Lending is more than a status: who has it and since
// when ride along, so leaving Lent out must clear them or the next lend would show the old borrower.
import type { ItemStatus, UpdateItemRequest } from '@/api/generated/models';

/**
 * `from` is the item's status now, `to` the one chosen, `today` is YYYY-MM-DD.
 * Choosing Lent out leaves the name blank (the edit form does the same) and starts the date today.
 */
export function statusPatch(from: ItemStatus, to: ItemStatus, today: string): UpdateItemRequest {
  if (to !== 'lent') return { status: to, lentTo: null, lentOn: null };
  if (from === 'lent') return { status: 'lent' };
  return { status: 'lent', lentTo: null, lentOn: today };
}
