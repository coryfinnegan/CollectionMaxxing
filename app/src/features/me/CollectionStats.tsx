// The Great Hall (#363): Stats as the home of the collection. A battlement header
// names the hall, the hoard pile grows with the high-water mark and never
// shrinks, milestone arms mark the rungs at 10 / 50 / 100 / 250 / 500 / 1000 for
// the whole shelves and per medium, series shields name the works shelved more
// than once, and the top milestone gets a share card. Everything still derives
// from existing API data. Plain words first; heraldry never replaces clarity.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ReleaseFormat, WorkKind } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { formatLabels, kindLabels } from '@/features/library/labels';
import { useLocationsList } from '@/features/locations/useLocations';
import { Button, Card, EmptyState, ListRow, Text } from '@/ui';
import { BannerMarker, CastleHeader, CastleState, HoardMeter, LedgeRow, SeriesShield, useHoardPeak } from '@/ui/castle';
import { earnedMilestones, nextMilestone } from './milestones';
import { shareMilestone } from './shareMilestone';
import { useCollectionStats } from './useCollectionStats';

/** The highest counts first, skipping zeroes. */
const top = (counts: Record<string, number>, n: number): [string, number][] =>
  Object.entries(counts)
    .filter(([, count]) => count > 0)
    .sort((a, b) => b[1] - a[1])
    .slice(0, n);

export function CollectionStats({ libraryId }: { libraryId: string }) {
  const router = useRouter();
  const { stats, isLoading, recent, recentReady, locationCounts, locationsReady, multiCopy } = useCollectionStats(libraryId);
  const { locations, isLoading: locationsLoading } = useLocationsList(libraryId);
  if (isLoading || !stats) return null;
  if (stats.total === 0) {
    return (
      <CastleState pose="emptyHoard">
        <EmptyState title="Nothing here yet." body="Scan something and it shows up here." bodyTone="ink" />
      </CastleState>
    );
  }
  const shelvesReady = locationsReady && !locationsLoading;
  const shelves = shelvesReady ? locationCounts.slice(0, 3) : [];
  const shelved = locationCounts.reduce((sum, shelf) => sum + shelf.count, 0);
  const fullest = locationCounts[0];
  const mostLivesOn = shelvesReady && fullest && stats.total >= 5 && fullest.count / Math.max(1, shelved) >= 0.5 ? breadcrumb(locations, fullest.locationId) : null;
  return (
    <Hall
      libraryId={libraryId}
      total={stats.total}
      byKind={stats.byKind}
      byFormat={stats.byFormat}
      recent={recent}
      recentReady={recentReady}
      shelves={shelves.map(({ locationId, count }) => ({ name: breadcrumb(locations, locationId), count }))}
      mostLivesOn={mostLivesOn}
      multiCopy={multiCopy}
      onOpenItem={(id) => router.push(`/item/${id}`)}
    />
  );
}

type HallProps = {
  libraryId: string;
  total: number;
  byKind: Record<string, number>;
  byFormat: Record<string, number>;
  recent: { id: string; title: string; format: ReleaseFormat }[];
  recentReady: boolean;
  shelves: { name: string; count: number }[];
  mostLivesOn: string | null;
  multiCopy: { workId: string; title: string; copies: number }[];
  onOpenItem(id: string): void;
};

function Hall({ libraryId, total, byKind, byFormat, recent, recentReady, shelves, mostLivesOn, multiCopy, onOpenItem }: HallProps) {
  const peak = useHoardPeak(libraryId, total);
  const earned = earnedMilestones(total);
  const next = nextMilestone(total);
  const mediaArms = top(byKind, 5)
    .map(([kind, count]) => ({ kind: kind as WorkKind, highest: earnedMilestones(count).at(-1) }))
    .filter((arm): arm is { kind: WorkKind; highest: number } => arm.highest !== undefined);
  const [shareMessage, setShareMessage] = useState<string | null>(null);
  const highest = earned.at(-1);

  const share = async () => {
    if (highest === undefined) return;
    setShareMessage(null);
    const result = await shareMilestone(`I shelved ${highest} items in my CollectionMaxxing library.`);
    if (result === 'copied') setShareMessage('Copied to the clipboard.');
    else if (result === 'unavailable') setShareMessage('Sharing is not available here.');
  };

  return (
    <View style={styles.hall}>
      <CastleHeader title="Great hall" subtitle={next ? `${next.toGo} more to reach ${next.at}` : 'Every arm is raised'} />
      <Card>
        <HoardMeter peak={peak} total={total} />
      </Card>
      <Card>
        <BannerMarker label="Milestones" />
        {earned.map((rung) => (
          <SeriesShield key={rung} label={`${rung} items`} sublabel="Whole collection" testID={`milestone-${rung}`} />
        ))}
        {mediaArms.map(({ kind, highest: rung }) => (
          <SeriesShield key={kind} label={kindLabels[kind] ?? kind} sublabel={`${rung} shelved`} testID={`milestone-${kind}`} />
        ))}
        {next ? <Text tone="muted">{`${next.toGo} more to reach ${next.at}.`}</Text> : <Text tone="muted">Every arm is raised.</Text>}
      </Card>
      <Card>
        <BannerMarker label="Breakdown" />
        {top(byKind, 3).map(([kind, count]) => {
          const label = kindLabels[kind as WorkKind] ?? kind;
          return <ListRow key={kind} title={label} testID={`stat-${label}`} trailing={<MonoCount count={count} />} />;
        })}
        {top(byFormat, 3).map(([format, count]) => {
          const label = formatLabels[format as ReleaseFormat] ?? format;
          return <ListRow key={format} title={label} testID={`stat-${label}`} trailing={<MonoCount count={count} />} />;
        })}
      </Card>
      {recentReady && recent.length > 0 ? (
        <Card>
          <BannerMarker label="Newest" />
          {recent.slice(0, 3).map((item) => (
            <LedgeRow key={item.id} title={item.title} subtitle={formatLabels[item.format]} onPress={() => onOpenItem(item.id)} />
          ))}
        </Card>
      ) : null}
      {shelves.length > 0 ? (
        <Card>
          <BannerMarker label="Fullest shelves" />
          {shelves.map(({ name, count }) => (
            <ListRow key={name} title={name} testID={`shelf-${name}`} trailing={<MonoCount count={count} />} />
          ))}
          {mostLivesOn ? <Text tone="muted">Most of it lives on {mostLivesOn}.</Text> : null}
        </Card>
      ) : null}
      {multiCopy.length > 0 ? (
        <Card>
          <BannerMarker label="Shelved more than once" />
          {multiCopy.slice(0, 3).map((work) => (
            <SeriesShield key={work.workId} label={work.title} sublabel={`${work.copies} copies`} testID={`series-${work.workId}`} />
          ))}
          {multiCopy.length > 3 ? <Text tone="muted">{`And ${multiCopy.length - 3} more.`}</Text> : null}
        </Card>
      ) : null}
      {highest !== undefined ? (
        <Card>
          <Text variant="heading">You shelved {highest} items</Text>
          {shareMessage ? <Text tone="muted">{shareMessage}</Text> : null}
          <Button label="Share this milestone" variant="secondary" onPress={() => void share()} />
        </Card>
      ) : null}
    </View>
  );
}

function MonoCount({ count }: { count: number }) {
  return (
    <Text variant="mono" tone="muted">
      {count}
    </Text>
  );
}

const styles = StyleSheet.create((theme) => ({
  hall: { gap: theme.spacing.md },
}));
