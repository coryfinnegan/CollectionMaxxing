// Moving an item to another status from somewhere that does not hold its version (a scan card): read it, send the one PATCH with
// that version, and keep what Undo needs. The item screen has its own version in hand and sends the same patches itself.
import { getItem, updateItem } from '@/api/generated/items/items';
import type { UpdateItemRequest } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { ifMatchHeader } from './etag';
import { patchFor, restoreOf, STATUS_MOVES, type StatusMoveName } from './statusMoves';

/** A move that happened: what it says, and what takes it back. */
export type MovedItem = {
  itemId: string;
  message: string;
  /** The status the move produced; Undo only goes ahead while the item is still in it. */
  to: string;
  restore: UpdateItemRequest;
};

/** Moves the item with one PATCH. Throws ApiError (412 when someone changed it between the read and the write). */
export async function moveItem(libraryId: string, itemId: string, name: StatusMoveName): Promise<MovedItem> {
  const loaded = await getItem(libraryId, itemId);
  if (loaded.status !== 200) throw new ApiError(loaded.status, {});
  const etag = loaded.headers.get('etag');
  if (!etag) throw new ApiError(500, {});
  const patch = patchFor(name);
  const patched = await updateItem(libraryId, itemId, patch, { headers: ifMatchHeader(etag) });
  if (patched.status !== 200) throw new ApiError(patched.status, {});
  return { itemId, message: STATUS_MOVES[name].done, to: STATUS_MOVES[name].to, restore: restoreOf(loaded.data.item, patch) };
}

/** Takes a move back, unless the item was changed since (it then stays as it is: 412). */
export async function undoItemMove(libraryId: string, moved: MovedItem): Promise<void> {
  const current = await getItem(libraryId, moved.itemId);
  if (current.status !== 200) throw new ApiError(current.status, {});
  if (current.data.item.status !== moved.to) throw new ApiError(412, {});
  const etag = current.headers.get('etag');
  if (!etag) throw new ApiError(500, {});
  const patched = await updateItem(libraryId, moved.itemId, moved.restore, { headers: ifMatchHeader(etag) });
  if (patched.status !== 200) throw new ApiError(patched.status, {});
}
