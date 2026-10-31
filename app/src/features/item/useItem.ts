// Hooks for one item: load it with its ETag, then update, delete and restore it.
// The generated mutation hooks can't take per-call headers, so the writes call the generated
// functions directly and send If-Match / Idempotency-Key themselves.
import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { create } from 'zustand';
import { createItem, deleteItem, restoreItem, updateItem, useGetItem } from '@/api/generated/items/items';
import type { CreateItemRequest, ItemDetailDto, UpdateItemRequest } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { ifMatchHeader } from './etag';

/** The item and the ETag it was loaded with. The ETag goes back as If-Match on PATCH and DELETE. */
export function useItemDetail(libraryId: string | undefined, id: string) {
  const query = useGetItem(libraryId ?? '', id, { query: { enabled: !!libraryId } });
  const ok = query.data?.status === 200 ? query.data : undefined;
  const detail: ItemDetailDto | undefined = ok?.data;
  const etag = ok?.headers.get('etag') ?? undefined;
  return { detail, etag, isLoading: query.isLoading, isError: query.isError, error: query.error, refetch: query.refetch };
}

/** Refreshes every list and detail query for the library after a change. */
export function invalidateLibraryItems(queryClient: QueryClient, libraryId: string) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['library-items', libraryId] }),
    // Item detail keys are one string per item (".../items/<id>"), so match by prefix, not by array element.
    queryClient.invalidateQueries({
      predicate: ({ queryKey }) => typeof queryKey[0] === 'string' && queryKey[0].startsWith(`/api/v1/libraries/${libraryId}/items`),
    }),
    queryClient.invalidateQueries({ queryKey: [`/api/v1/libraries/${libraryId}/stats`] }),
  ]);
}

/** Refreshes every list and detail query for the library after a change. */
export function useInvalidateLibrary() {
  const queryClient = useQueryClient();
  return (libraryId: string) => invalidateLibraryItems(queryClient, libraryId);
}

export function useItemMutations(libraryId: string) {
  const queryClient = useQueryClient();
  const invalidate = useInvalidateLibrary();
  const onSettled = () => invalidate(libraryId);
  return {
    create: useMutation({
      mutationFn: ({ data, idempotencyKey }: { data: CreateItemRequest; idempotencyKey: string }) =>
        createItem(libraryId, data, { headers: { 'Idempotency-Key': idempotencyKey } }),
      // A search result the server no longer keeps: ask the search again, which shows (and keeps) its hits afresh.
      onError: (error) => {
        if (error instanceof ApiError && error.problem.code === 'provider_hit.not_found') {
          void queryClient.invalidateQueries({ queryKey: ['/api/v1/catalog/search'] });
        }
      },
      onSettled,
    }),
    update: useMutation({
      mutationFn: ({ id, data, etag }: { id: string; data: UpdateItemRequest; etag: string }) =>
        updateItem(libraryId, id, data, { headers: ifMatchHeader(etag) }),
      onSettled,
    }),
    remove: useMutation({
      mutationFn: ({ id, etag }: { id: string; etag: string }) =>
        deleteItem(libraryId, id, { headers: ifMatchHeader(etag) }),
      onSettled,
    }),
    restore: useMutation({
      mutationFn: ({ id }: { id: string }) => restoreItem(libraryId, id),
      onSettled,
    }),
  };
}

/** The last deleted item, so Undo can be offered on whichever screen is showing. */
type UndoState = {
  deleted: { libraryId: string; id: string; title: string } | null;
  setDeleted: (deleted: UndoState['deleted']) => void;
};

export const useUndoStore = create<UndoState>((set) => ({
  deleted: null,
  setDeleted: (deleted) => set({ deleted }),
}));
