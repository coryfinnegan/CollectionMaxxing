// The library list: cursor pages from GET /items, one infinite query per set of filters.
import { useInfiniteQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { listItems } from '@/api/generated/items/items';
import type { ItemStatus, ItemSummaryDto } from '@/api/generated/models';
import { toListParams, type ListState } from './query';

export function useItems(libraryId: string | undefined, state: ListState, fixedStatus?: ItemStatus) {
  // The cursor is part of the request, not of the key: each filter set gets its own list of pages.
  const base = toListParams({ ...state, cursor: undefined }, fixedStatus);
  const query = useInfiniteQuery({
    queryKey: ['library-items', libraryId, base],
    enabled: !!libraryId,
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const response = await listItems(libraryId ?? '', { ...base, cursor: pageParam }, { signal });
      return response.data as { items: ItemSummaryDto[]; nextCursor?: string | null };
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const items = query.data?.pages.flatMap((page) => page.items) ?? [];
  return {
    items,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
    isRefreshing: query.isRefetching && !query.isFetchingNextPage,
    hasNextPage: query.hasNextPage,
    fetchNextPage: query.fetchNextPage,
    isFetchingNextPage: query.isFetchingNextPage,
    refetch: query.refetch,
  };
}

/** The value, a moment after it stops changing (for the search box). */
export function useDebounced<T>(value: T, ms = 250): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms);
    return () => clearTimeout(timer);
  }, [value, ms]);
  return debounced;
}
