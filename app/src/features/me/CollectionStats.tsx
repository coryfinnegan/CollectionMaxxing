// The Me tab's shelves block (#306): the shape of the collection in the Phase 0
// type system, mono numerals. Everything derives from existing API data.
import { useRouter } from 'expo-router';
import type { ReleaseFormat, WorkKind } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { formatLabels, kindLabels } from '@/features/library/labels';
import { useLocationsList } from '@/features/locations/useLocations';
import { Card, ListRow, Text } from '@/ui';
import { useCollectionStats } from './useCollectionStats';

/** The highest counts first, skipping zeroes. */
const top = (counts: Record<string, number>, n: number): [string, number][] =>
  Object.entries(counts)
    .filter(([, count]) => count > 0)
    .sort((a, b) => b[1] - a[1])
    .slice(0, n);

export function CollectionStats({ libraryId }: { libraryId: string }) {
  const router = useRouter();
  const { stats, isLoading, recent, recentReady, locationCounts, locationsReady } = useCollectionStats(libraryId);
  const { locations, isLoading: locationsLoading } = useLocationsList(libraryId);
  if (isLoading || !stats) return null;
  if (stats.total === 0) {
    return (
      <Card>
        <Text variant="heading">Your shelves</Text>
        <Text tone="muted">Nothing here yet.</Text>
      </Card>
    );
  }
  const shelvesReady = locationsReady && !locationsLoading;
  const shelves = shelvesReady ? locationCounts.slice(0, 3) : [];
  const shelved = locationCounts.reduce((sum, shelf) => sum + shelf.count, 0);
  const fullest = locationCounts[0];
  const mostLivesOn = shelvesReady && fullest && stats.total >= 5 && fullest.count / Math.max(1, shelved) >= 0.5 ? breadcrumb(locations, fullest.locationId) : null;
  return (
    <Card>
      <Text variant="heading">Your shelves</Text>
      {top(stats.byKind, 3).map(([kind, count]) => {
        const label = kindLabels[kind as WorkKind] ?? kind;
        return <ListRow key={kind} title={label} testID={`stat-${label}`} trailing={<MonoCount count={count} />} />;
      })}
      {top(stats.byFormat, 3).map(([format, count]) => {
        const label = formatLabels[format as ReleaseFormat] ?? format;
        return <ListRow key={format} title={label} testID={`stat-${label}`} trailing={<MonoCount count={count} />} />;
      })}
      {recentReady && recent.length > 0 ? <Text variant="label">Newest</Text> : null}
      {recentReady
        ? recent.slice(0, 3).map((item) => (
            <ListRow key={item.id} title={item.title} subtitle={formatLabels[item.format]} onPress={() => router.push(`/item/${item.id}`)} />
          ))
        : null}
      {shelves.length > 0 ? <Text variant="label">Fullest shelves</Text> : null}
      {shelves.map(({ locationId, count }) => {
        const name = breadcrumb(locations, locationId);
        return <ListRow key={locationId} title={name} testID={`shelf-${name}`} trailing={<MonoCount count={count} />} />;
      })}
      {mostLivesOn ? <Text tone="muted">Most of it lives on {mostLivesOn}.</Text> : null}
    </Card>
  );
}

function MonoCount({ count }: { count: number }) {
  return (
    <Text variant="mono" tone="muted">
      {count}
    </Text>
  );
}
