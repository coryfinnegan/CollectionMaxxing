// The library as a grid of covers with search, filter chips, sort, pull to refresh and infinite scroll.
// Wishlist and Lent out are the same view with a fixed status.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { FlatList, Pressable, ScrollView, useWindowDimensions, View } from 'react-native';
import { useGetStats } from '@/api/generated/items/items';
import type { ItemStatus, ItemSummaryDto } from '@/api/generated/models';
import { UndoSnackbar } from '@/features/item/UndoSnackbar';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { flattenTree } from '@/features/locations/tree';
import { friendlyError } from '@/lib/problem';
import { spacing, useColors, WIDE_BREAKPOINT } from '@/theme/tokens';
import { Button, Chip, Cover, EmptyState, FormatBadge, ListRow, Sheet, Text, TextField } from '@/ui';
import { AddItemPanel } from './AddItemPanel';
import { formatLabels, formats, kindLabels, kinds } from './labels';
import { changeFilter, defaultState, hasActiveFilter, type ListState, sortLabel, sortOptions } from './query';
import { useDebounced, useItems } from './useItems';

export type LibraryGridProps = {
  title: string;
  /** Set for the Wishlist and Lent out views. */
  fixedStatus?: ItemStatus;
  /** Opens with the Add an item panel showing (the Scan tab's "Add an item by hand"). */
  openAdd?: boolean;
};

type Panel = 'format' | 'platform' | 'location' | 'sort' | 'add' | null;

const emptyCopy: Record<string, { title: string; body: string }> = {
  library: { title: 'Nothing here yet.', body: 'Point your camera at a shelf or a stack of cases.' },
  wishlist: { title: 'Your wishlist is empty.', body: 'Add something you want to own and it shows up here.' },
  lent: { title: 'Nothing is lent out.', body: 'Lend an item from its page and it shows up here.' },
};

export function LibraryGrid({ title, fixedStatus, openAdd }: LibraryGridProps) {
  const { libraryId, isLoading: loadingLibrary } = useActiveLibraryId();
  if (loadingLibrary || !libraryId) {
    return (
      <View style={{ padding: spacing.lg, gap: spacing.md }}>
        <Text variant="title" accessibilityRole="header">
          {title}
        </Text>
        <Text tone="muted">Loading your library…</Text>
      </View>
    );
  }
  return <Grid libraryId={libraryId} title={title} fixedStatus={fixedStatus} openAdd={openAdd} />;
}

function Grid({ libraryId, title, fixedStatus, openAdd }: LibraryGridProps & { libraryId: string }) {
  const router = useRouter();
  const colors = useColors();
  const { width } = useWindowDimensions();
  const columns = width >= WIDE_BREAKPOINT ? 6 : 3;
  const tileWidth = Math.floor((width - spacing.lg * 2 - spacing.md * (columns - 1)) / columns);

  const [chosen, setState] = useState<ListState>(defaultState());
  const [search, setSearch] = useState('');
  const [panel, setPanel] = useState<Panel>(openAdd ? 'add' : null);
  const debouncedSearch = useDebounced(search);
  // The search box applies a moment after typing stops; everything else applies at once.
  const state = changeFilter(chosen, { q: debouncedSearch });

  const list = useItems(libraryId, state, fixedStatus);
  const stats = useGetStats(libraryId);
  const counts = stats.data?.status === 200 ? stats.data.data : undefined;
  const { locations } = useLocationsList(libraryId);
  const { filters } = state;
  const filter = (patch: Partial<typeof filters>) => setState((current) => changeFilter(current, patch));
  const locationName = locations.find((l) => l.id === filters.locationId)?.name;
  const view = fixedStatus === 'wishlist' ? 'wishlist' : fixedStatus === 'lent' ? 'lent' : 'library';

  const closeAnd = (patch: Partial<typeof filters>) => {
    filter(patch);
    setPanel(null);
  };

  const header = (
    <View style={{ gap: spacing.md, paddingBottom: spacing.md }}>
      <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
        <Text variant="title" accessibilityRole="header" style={{ flex: 1 }}>
          {title}
        </Text>
        <Button label="Add item" variant="secondary" onPress={() => setPanel(panel === 'add' ? null : 'add')} />
      </View>

      {panel === 'add' ? <AddItemPanel libraryId={libraryId} onClose={() => setPanel(null)} /> : null}

      <TextField
        label="Search"
        value={search}
        onChangeText={setSearch}
        placeholder={counts ? `Search ${counts.total} items` : 'Search your library'}
        returnKeyType="search"
        autoCapitalize="none"
        autoCorrect={false}
      />

      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ gap: spacing.sm }}>
        <Chip label="All" selected={!filters.kind} onPress={() => filter({ kind: undefined })} />
        {kinds.map((kind) => {
          const count = counts?.byKind[kind];
          return (
            <Chip
              key={kind}
              label={count ? `${kindLabels[kind]} ${count}` : kindLabels[kind]}
              accessibilityLabel={kindLabels[kind]}
              selected={filters.kind === kind}
              onPress={() => filter({ kind: filters.kind === kind ? undefined : kind })}
            />
          );
        })}
      </ScrollView>

      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ gap: spacing.sm }}>
        <Chip
          label={filters.format ? formatLabels[filters.format] : 'Any format'}
          accessibilityLabel="Filter by format"
          selected={!!filters.format}
          onPress={() => setPanel('format')}
        />
        <Chip
          label={filters.platform ?? 'Any platform'}
          accessibilityLabel="Filter by platform"
          selected={!!filters.platform}
          onPress={() => setPanel('platform')}
        />
        <Chip
          label={locationName ?? 'Any location'}
          accessibilityLabel="Filter by location"
          selected={!!filters.locationId}
          onPress={() => setPanel('location')}
        />
        <Chip label={`Sort: ${sortLabel(filters.sort)}`} accessibilityLabel="Sort" onPress={() => setPanel('sort')} />
      </ScrollView>

      {panel === 'format' ? (
        <Sheet title="Format" onClose={() => setPanel(null)}>
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
            <Chip label="Any format" selected={!filters.format} onPress={() => closeAnd({ format: undefined })} />
            {formats.map((format) => (
              <Chip
                key={format}
                label={formatLabels[format]}
                selected={filters.format === format}
                onPress={() => closeAnd({ format })}
              />
            ))}
          </View>
        </Sheet>
      ) : null}

      {panel === 'platform' ? (
        <Sheet title="Platform" onClose={() => setPanel(null)}>
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
            <Chip label="Any platform" selected={!filters.platform} onPress={() => closeAnd({ platform: undefined })} />
            {Object.keys(counts?.byPlatform ?? {}).map((platform) => (
              <Chip
                key={platform}
                label={platform}
                selected={filters.platform === platform}
                onPress={() => closeAnd({ platform })}
              />
            ))}
          </View>
        </Sheet>
      ) : null}

      {panel === 'location' ? (
        <Sheet title="Location" onClose={() => setPanel(null)}>
          <ListRow title="Any location" onPress={() => closeAnd({ locationId: undefined })} />
          {flattenTree(locations).map(({ location, depth }) => (
            <ListRow
              key={location.id}
              title={location.name}
              depth={depth}
              onPress={() => closeAnd({ locationId: location.id })}
            />
          ))}
        </Sheet>
      ) : null}

      {panel === 'sort' ? (
        <Sheet title="Sort" onClose={() => setPanel(null)}>
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
            {sortOptions.map((option) => (
              <Chip
                key={option.value}
                label={option.label}
                selected={filters.sort === option.value}
                onPress={() => closeAnd({ sort: option.value })}
              />
            ))}
          </View>
        </Sheet>
      ) : null}
    </View>
  );

  const clearFilters = () => {
    setSearch('');
    setState(defaultState());
  };

  const empty = list.isError ? (
    <EmptyState
      title="Couldn't load your library"
      body={friendlyError(list.error)}
      actionLabel="Try again"
      onAction={() => list.refetch()}
    />
  ) : list.isLoading ? (
    <Text tone="muted">Loading your library…</Text>
  ) : hasActiveFilter(filters) ? (
    <EmptyState
      title="Nothing matches."
      body="Try a different search or filter."
      actionLabel="Clear filters"
      onAction={clearFilters}
    />
  ) : (
    <EmptyState
      title={emptyCopy[view].title}
      body={emptyCopy[view].body}
      actionLabel={view === 'library' ? 'Scan your first shelf' : undefined}
      onAction={view === 'library' ? () => router.push('/scan') : undefined}
    />
  );

  return (
    <View style={{ flex: 1, backgroundColor: colors.ground }}>
      <FlatList<ItemSummaryDto>
        key={columns}
        testID="library-grid"
        data={list.items}
        numColumns={columns}
        keyExtractor={(item) => item.id}
        contentContainerStyle={{ padding: spacing.lg, gap: spacing.lg }}
        columnWrapperStyle={{ gap: spacing.md }}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        refreshing={list.isRefreshing}
        onRefresh={() => list.refetch()}
        onEndReachedThreshold={0.5}
        onEndReached={() => {
          if (list.hasNextPage && !list.isFetchingNextPage) list.fetchNextPage();
        }}
        ListFooterComponent={list.isFetchingNextPage ? <Text tone="muted">Loading more…</Text> : null}
        renderItem={({ item }) => (
          <ItemTile item={item} width={tileWidth} onPress={() => router.push(`/item/${item.id}`)} />
        )}
      />
      <View style={{ padding: spacing.lg }}>
        <UndoSnackbar />
      </View>
    </View>
  );
}

function ItemTile({ item, width, onPress }: { item: ItemSummaryDto; width: number; onPress: () => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${item.title}, ${details}`}
      onPress={onPress}
      testID={`item-${item.id}`}
      style={{ width, gap: spacing.xs }}
    >
      <Cover title={item.title} uri={item.coverUrl} width={width} />
      <Text variant="label" numberOfLines={2}>
        {item.title}
      </Text>
      <FormatBadge label={formatLabels[item.format]} />
    </Pressable>
  );
}
