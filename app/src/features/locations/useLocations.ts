// Reference slice hooks: the generated TanStack Query hooks plus optimistic updates for add, rename and delete.
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useGetMe } from '@/api/generated/account/account';
import {
  getListLocationsQueryKey,
  useCreateLocation,
  useDeleteLocation,
  useListLocations,
  useUpdateLocation,
} from '@/api/generated/locations/locations';
import type { LocationDto } from '@/api/generated/models';
import { useLibraryStore } from '@/state/library';

type Cached = { data: { items: LocationDto[]; nextCursor?: string | null }; status: number; headers: Headers };

/** The library the app is showing: the stored choice, else the first membership from GET /me. */
export function useActiveLibraryId(): { libraryId: string | undefined; isLoading: boolean } {
  const me = useGetMe();
  const stored = useLibraryStore((s) => s.activeLibraryId);
  const setStored = useLibraryStore((s) => s.setActiveLibraryId);
  const memberships = me.data?.status === 200 ? me.data.data.memberships : [];
  const first = memberships[0]?.libraryId;
  useEffect(() => {
    if (!stored && first) setStored(first);
  }, [stored, first, setStored]);
  return { libraryId: stored ?? first, isLoading: me.isLoading };
}

export function useLocationsList(libraryId: string | undefined) {
  const query = useListLocations(libraryId ?? '', { query: { enabled: !!libraryId } });
  const locations: LocationDto[] = query.data?.status === 200 ? query.data.data.items : [];
  return { locations, isLoading: query.isLoading, isError: query.isError, refetch: query.refetch };
}

export function useLocationMutations(libraryId: string) {
  const queryClient = useQueryClient();
  const key = getListLocationsQueryKey(libraryId);

  // Change the cached list right away; roll back if the server says no; refetch to settle on the truth.
  const optimistic = (change: (items: LocationDto[]) => LocationDto[]) => async () => {
    await queryClient.cancelQueries({ queryKey: key });
    const previous = queryClient.getQueryData<Cached>(key);
    if (previous) {
      queryClient.setQueryData<Cached>(key, {
        ...previous,
        data: { ...previous.data, items: change(previous.data.items) },
      });
    }
    return { previous };
  };
  const rollback = (_error: unknown, _vars: unknown, context: { previous?: Cached } | undefined) => {
    if (context?.previous) queryClient.setQueryData(key, context.previous);
  };
  const settle = () => queryClient.invalidateQueries({ queryKey: key });

  const create = useCreateLocation({
    mutation: {
      onMutate: ({ data }) =>
        optimistic((items) => [
          ...items,
          {
            id: `pending-${Date.now()}`,
            parentId: data.parentId ?? null,
            name: data.name?.trim() ?? '',
            sortOrder: items.length,
          },
        ])(),
      onError: rollback,
      onSettled: settle,
    },
  });

  const rename = useUpdateLocation({
    mutation: {
      onMutate: ({ id, data }) =>
        optimistic((items) => items.map((l) => (l.id === id ? { ...l, name: data.name?.trim() ?? l.name } : l)))(),
      onError: rollback,
      onSettled: settle,
    },
  });

  const remove = useDeleteLocation({
    mutation: {
      onMutate: ({ id }) => optimistic((items) => items.filter((l) => l.id !== id))(),
      onError: rollback,
      onSettled: settle,
    },
  });

  return { create, rename, remove };
}
