// "Moved to Wishlist" / "Moved to Sold", with Undo: what the table says when a status change takes a row out of the library (its row is
// gone from this list the moment the list refreshes, so the words and the way back live here, not in the cell). The same words the item
// screen uses. Undo sends the old status back with the version the change produced; if someone changed the item since, it stays as it is.
import { useEffect } from 'react';
import type { UpdateItemRequest } from '@/api/generated/models';
import { updateItem } from '@/api/generated/items/items';
import { ifMatchHeader } from '@/features/item/etag';
import { useInvalidateLibrary } from '@/features/item/useItem';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { Snackbar } from '@/ui';

/** A status change that took an item out of the library. `restore` is null when the old state cannot be rebuilt (it was lent, to no one by name). */
export type TableMove = { itemId: string; message: string; restore: UpdateItemRequest | null; etag: string | null };

const VISIBLE_MS = 10_000;

export const UNDO_REFUSED = "Couldn't undo. It changed since, so it stays as it is.";

/** `onClear` must be stable (the table passes one from `useCallback`), or the toast's own timer would start over on every render. */
export function MovedToast({
  libraryId,
  moved,
  onClear,
  onNotice,
}: {
  libraryId: string;
  moved: TableMove;
  onClear: () => void;
  onNotice: (text: string) => void;
}) {
  const invalidate = useInvalidateLibrary();
  useEffect(() => {
    const timer = setTimeout(onClear, VISIBLE_MS);
    return () => clearTimeout(timer);
  }, [moved, onClear]);

  const canUndo = !!moved.restore && !!moved.etag;
  const undo = async () => {
    if (!moved.restore || !moved.etag) return;
    onClear();
    try {
      await updateItem(libraryId, moved.itemId, moved.restore, { headers: ifMatchHeader(moved.etag) });
    } catch (error) {
      onNotice(error instanceof ApiError && error.status === 412 ? UNDO_REFUSED : friendlyError(error));
    } finally {
      await invalidate(libraryId);
    }
  };

  return <Snackbar message={moved.message} actionLabel={canUndo ? 'Undo' : undefined} onAction={canUndo ? () => void undo() : undefined} />;
}
