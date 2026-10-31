// Moving an item between the library, the wishlist and sold, in one tap: the PATCH each move sends, the words it uses, and
// the patch that takes it back. Plain data and functions, so the item screen, the scan card and the tests share them.
//
// Have it (owned, lent, missing), want it (wishlist), gone (sold): see features/library/statuses.ts.
import type { ItemDto, ItemStatus, UpdateItemRequest } from '@/api/generated/models';
import { localDateString } from '@/lib/dates';

export type StatusMoveName = 'got-it' | 'sold' | 'missing' | 'found' | 'got-back';

type MoveSpec = {
  /** The button, verb first. */
  label: string;
  /** What the item becomes. */
  to: ItemStatus;
  /** The toast, once it worked. */
  done: string;
};

export const STATUS_MOVES: Record<StatusMoveName, MoveSpec> = {
  'got-it': { label: 'I got it', to: 'owned', done: 'In your library' },
  sold: { label: 'Sold or gave it away', to: 'sold', done: 'Moved to Sold' },
  missing: { label: 'Mark as missing', to: 'missing', done: 'Marked as missing' },
  found: { label: 'Found it', to: 'owned', done: 'Back in your library' },
  'got-back': { label: 'Got it back', to: 'owned', done: 'Back in your library' },
};

/**
 * What the item screen offers for an item in this state: the one primary move that fits (the wishlist's I got it, a missing
 * item's Found it, what is sold or lent out's Got it back), and the quieter moves an owned item has.
 */
export function movesFor(status: ItemStatus): { primary: StatusMoveName | null; more: StatusMoveName[] } {
  switch (status) {
    case 'wishlist':
      return { primary: 'got-it', more: [] };
    case 'missing':
      return { primary: 'found', more: [] };
    case 'sold':
    case 'lent':
      return { primary: 'got-back', more: [] };
    default:
      return { primary: null, more: ['missing', 'sold'] };
  }
}

/** The one PATCH a move sends. Getting a wishlisted item stamps the day you got it, in the device's own day. */
export function patchFor(name: StatusMoveName, today: string = localDateString()): UpdateItemRequest {
  const patch: UpdateItemRequest = { status: STATUS_MOVES[name].to };
  if (name === 'got-it') patch.acquiredOn = today;
  return patch;
}

/**
 * The patch that puts the item back as it was before the move: its old status, the day it was acquired if the move stamped one, and
 * the borrower if it was lent out (leaving Lent out clears them on the server).
 */
export function restoreOf(before: ItemDto, sent: UpdateItemRequest): UpdateItemRequest {
  const restore: UpdateItemRequest = { status: before.status };
  if (sent.acquiredOn !== undefined) restore.acquiredOn = before.acquiredOn;
  if (before.status === 'lent') {
    restore.lentTo = before.lentTo;
    restore.lentOn = before.lentOn;
  }
  return restore;
}
