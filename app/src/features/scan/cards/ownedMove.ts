// "Move it here" on an owned card: a location-only patch through the item-update endpoint (design §7).
// Moving writes nothing but the location; undoing is the same patch back. Plain functions, so the hook and the
// tests share them; the hook holds the toast. The first exact copy moves (the server orders them oldest first).
import type { QueryClient } from '@tanstack/react-query';
import { getItem, updateItem } from '@/api/generated/items/items';
import { getOwnership } from '@/api/generated/ownership/ownership';
import { ifMatchHeader } from '@/features/item/etag';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { syncLibrary } from '@/features/offline/sync';
import { ApiError } from '@/lib/http';

export type MovedCopy = {
  itemId: string;
  /** Where it was: null is "Not placed". Undo patches this back. */
  previousLocationId: string | null;
  /** True when it was already there: no write happened, and there is nothing to undo. */
  alreadyThere: boolean;
};

/**
 * Which copy moves: the server's first exact copy of the release. When the server cannot answer (offline),
 * the owned index's copy, so the reason for the failure is the move itself, not the lookup.
 */
export async function resolveOwnedItemId(libraryId: string, releaseId: string, localItemId: string | null): Promise<string | null> {
  try {
    const response = await getOwnership(libraryId, { releaseId });
    if (response.status === 200) return response.data.copies[0]?.itemId ?? null;
  } catch {
    // No connection: the local copy answers.
  }
  return localItemId;
}

/** Moves the copy with a location-only patch. Throws ApiError (412 when someone changed it since). */
export async function moveCopyTo(libraryId: string, itemId: string, locationId: string | null): Promise<MovedCopy> {
  const loaded = await getItem(libraryId, itemId);
  if (loaded.status !== 200) throw new ApiError(loaded.status, {});
  const etag = loaded.headers.get('etag');
  if (!etag) throw new ApiError(500, {});
  const previousLocationId = loaded.data.item.locationId ?? null;
  if (previousLocationId === locationId) return { itemId, previousLocationId, alreadyThere: true };
  const patched = await updateItem(libraryId, itemId, { locationId }, { headers: ifMatchHeader(etag) });
  if (patched.status !== 200) throw new ApiError(patched.status, {});
  return { itemId, previousLocationId, alreadyThere: false };
}

/** After a move or its undo: the owned index, the ownership answers, and the library lists say where the copy is now. */
export async function refreshAfterMove(queryClient: QueryClient, libraryId: string): Promise<void> {
  await syncLibrary(libraryId);
  await invalidateLibraryItems(queryClient, libraryId);
  await queryClient.invalidateQueries({
    predicate: ({ queryKey }) => typeof queryKey[0] === 'string' && queryKey[0].startsWith(`/api/v1/libraries/${libraryId}/ownership`),
  });
}
