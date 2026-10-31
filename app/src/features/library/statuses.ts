// What an item's status means, in one place (the app's twin of the server's ItemStatuses).
//
// Have it: owned, lent and missing are the library. They count, and they answer "You own this" (missing is still yours,
// you just can't find it). Want it: wishlist is never in the library and never counts as owned. Gone: sold (sold, traded
// or given away) is never in the library either; it is kept so "didn't I have this?" has an answer.
import type { ItemStatus } from '@/api/generated/models';

/** The statuses that make up the library. */
export const IN_COLLECTION = ['owned', 'lent', 'missing'] as const satisfies readonly ItemStatus[];

/** The statuses that live beside the library: the wishlist, and what is gone. */
export const BESIDE_COLLECTION = ['wishlist', 'sold'] as const satisfies readonly ItemStatus[];

export type CollectionStatus = (typeof IN_COLLECTION)[number];

/** Whether an item with this status is in the library (and so counts as owned). */
export const isInCollection = (status: ItemStatus): status is CollectionStatus =>
  (IN_COLLECTION as readonly ItemStatus[]).includes(status);
