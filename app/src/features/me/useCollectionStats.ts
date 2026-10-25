// The stats behind the Me tab's shelves block (#306). Everything derives from
// existing endpoints: GET /stats for the type and format counts, one small
// items page for the newest titles, and a cursor walk (200 a page) that counts
// items per location on the phone.
import { useQuery } from '@tanstack/react-query';
import { listItems, useGetStats } from '@/api/generated/items/items';
import type { ItemSummaryDto, StatsDto } from '@/api/generated/models';

export type LocationCount = { locationId: string; count: number };

/** Every shelved item's location, counted on the phone, fullest first. */
export async function fetchLocationCounts(libraryId: string, signal?: AbortSignal): Promise<LocationCount[]> {
  const counts = new Map<string, number>();
  let cursor: string | undefined;
  for (;;) {
    const response = await listItems(libraryId, { sort: '-addedAt', limit: 200, cursor }, { signal });
    const page = response.data as { items: ItemSummaryDto[]; nextCursor?: string | null };
    for (const item of page.items) {
      if (item.locationId) counts.set(item.locationId, (counts.get(item.locationId) ?? 0) + 1);
    }
    if (!page.nextCursor) break;
    cursor = page.nextCursor;
  }
  return [...counts.entries()].map(([locationId, count]) => ({ locationId, count })).sort((a, b) => b.count - a.count);
}

export function useCollectionStats(libraryId: string | undefined) {
  const stats = useGetStats(libraryId ?? '', { query: { enabled: !!libraryId } });
  const recent = useQuery({
    queryKey: ['collection-recent', libraryId],
    enabled: !!libraryId,
    queryFn: async ({ signal }) => {
      const response = await listItems(libraryId ?? '', { sort: '-addedAt', limit: 5 }, { signal });
      return (response.data as { items: ItemSummaryDto[] }).items;
    },
  });
  const locations = useQuery({
    queryKey: ['collection-location-counts', libraryId],
    enabled: !!libraryId,
    // Shelves move slowly; the walk is exact but not worth repeating every visit.
    staleTime: 5 * 60_000,
    queryFn: ({ signal }) => fetchLocationCounts(libraryId ?? '', signal),
  });
  const dto: StatsDto | null = stats.data?.status === 200 ? stats.data.data : null;
  return {
    stats: dto,
    isLoading: stats.isLoading,
    recent: recent.data ?? [],
    recentReady: !recent.isLoading && !recent.isError,
    locationCounts: locations.data ?? [],
    locationsReady: !locations.isLoading && !locations.isError,
  };
}
