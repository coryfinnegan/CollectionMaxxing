// The PATCH body for changing one item's status from the table. Lending is more than a status: who has it and since
// when ride along, so leaving Lent out must clear them or the next lend would show the old borrower.
import type { ItemStatus, UpdateItemRequest } from '@/api/generated/models';

/** Whether choosing `to` for an item that is `from` now starts a lend, which needs the name of who has it. */
export function needsBorrower(from: ItemStatus, to: ItemStatus): boolean {
  return to === 'lent' && from !== 'lent';
}

/**
 * `from` is the item's status now, `to` the one chosen, `today` is YYYY-MM-DD.
 * Starting a lend (see `needsBorrower`) takes `lentTo`, the name of who has it: the server refuses a lend with no name,
 * so ask before calling this.
 */
export function statusPatch(from: ItemStatus, to: ItemStatus, today: string, lentTo = ''): UpdateItemRequest {
  if (to !== 'lent') return { status: to, lentTo: null, lentOn: null };
  if (from === 'lent') return { status: 'lent' };
  return { status: 'lent', lentTo: lentTo.trim(), lentOn: today };
}
