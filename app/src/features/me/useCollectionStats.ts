// The stats behind the Great Hall (#306, #363). Everything derives from existing
// endpoints: GET /stats for the type and format counts, one small items page for
// the newest titles, and a cursor walk (200 a page) that counts items per
// location and works shelved more than once on the phone.
import { useQuery } from '@tanstack/react-query';
import { listItems, useGetStats } from '@/api/generated/items/items';
import type { ItemSummaryDto, StatsDto } from '@/api/generated/models';

export type LocationCount = { locationId: string; count: number };

/** A work shelved more than once: the Great Hall's series shields (#363) name the top few. */
export type MultiCopyWork = { workId: string; title: string; copies: number };

export type ShelfTallies = { locations: LocationCount[]; multiCopy: MultiCopyWork[] };

/**
 * One cursor walk counting two things on the phone: every shelved item's
 * location (fullest first) and every work shelved more than once (most copies
 * first). Shelves move slowly, so callers cache the walk for minutes.
 */
export async function fetchShelfTallies(libraryId: string, signal?: AbortSignal): Promise<ShelfTallies> {
  const counts = new Map<string, number>();
  const works = new Map<string, { title: string; copies: number }>();
  let cursor: string | undefined;
  for (;;) {
    const response = await listItems(libraryId, { sort: '-addedAt', limit: 200, cursor }, { signal });
    const page = response.data as { items: ItemSummaryDto[]; nextCursor?: string | null };
    for (const item of page.items) {
      if (item.locationId) counts.set(item.locationId, (counts.get(item.locationId) ?? 0) + 1);
      const seen = works.get(item.workId);
      if (seen) seen.copies += 1;
      else works.set(item.workId, { title: item.title, copies: 1 });
    }
    if (!page.nextCursor) break;
    cursor = page.nextCursor;
  }
  const locations = [...counts.entries()]
    .map(([locationId, count]) => ({ locationId, count }))
    .sort((a, b) => b.count - a.count);
  const multiCopy = [...works.entries()]
    .filter(([, work]) => work.copies > 1)
    .map(([workId, work]) => ({ workId, ...work }))
    .sort((a, b) => b.copies - a.copies || a.title.localeCompare(b.title));
  return { locations, multiCopy };
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
  const tallies = useQuery({
    queryKey: ['collection-location-counts', libraryId],
    enabled: !!libraryId,
    // Shelves move slowly; the walk is exact but not worth repeating every visit.
    staleTime: 5 * 60_000,
    queryFn: ({ signal }) => fetchShelfTallies(libraryId ?? '', signal),
  });
  const dto: StatsDto | null = stats.data?.status === 200 ? stats.data.data : null;
  return {
    stats: dto,
    isLoading: stats.isLoading,
    recent: recent.data ?? [],
    recentReady: !recent.isLoading && !recent.isError,
    locationCounts: tallies.data?.locations ?? [],
    locationsReady: !tallies.isLoading && !tallies.isError,
    multiCopy: tallies.data?.multiCopy ?? [],
  };
}
