// The library as a grid of covers with search, filter chips, sort, pull to refresh and infinite scroll.
// Wishlist and Lent out are the same view with a fixed status.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { FlatList, Pressable, ScrollView, useWindowDimensions, View } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { SafeAreaView } from 'react-native-safe-area-context';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { useGetStats } from '@/api/generated/items/items';
import type { ItemStatus, ItemSummaryDto } from '@/api/generated/models';
import { UndoSnackbar } from '@/features/item/UndoSnackbar';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { flattenTree } from '@/features/locations/tree';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { friendlyError } from '@/lib/problem';
import { spacing, WIDE_BREAKPOINT } from '@/theme/tokens';
import { Button, Chip, Cover, EmptyState, FormatBadge, ListRow, LoadingState, Sheet, Text, TextField } from '@/ui';
import { maybeEntering, maybeLayout, motionMs, sharedCoverTag, staggerDelay } from '@/ui/motion';
import { AddItemPanel } from './AddItemPanel';
import { formatLabels, formats, kindLabels, kinds } from './labels';
import { NeedsAttention, useProvisionals } from './NeedsAttention';
import { changeFilter, defaultState, hasActiveFilter, type ListState, sortLabel, sortOptions } from './query';
import { useDebounced, useItems } from './useItems';

export type LibraryGridProps = {
  title: string;
  /** Set for the Wishlist and Lent out views. */
  fixedStatus?: ItemStatus;
  /** Opens with the Add an item panel showing (the Scan tab's "Add it by hand"). */
  openAdd?: boolean;
  /** Hides the content title: pushed pages on the phone already name themselves in the stack header (#303). */
  hideTitle?: boolean;
};

type Panel = 'format' | 'platform' | 'location' | 'sort' | 'add' | null;

/** The search box names the count, and reads naturally at any count ("Search your 1 item"). */
export const searchPlaceholder = (total: number | undefined) =>
  total === undefined ? 'Search your library' : total === 1 ? 'Search your 1 item' : `Search your ${total} items`;

const emptyCopy: Record<string, { title: string; body: string }> = {
  library: { title: 'Nothing here yet.', body: 'Point your camera at a shelf or a stack of cases.' },
  wishlist: { title: 'Your wishlist is empty.', body: 'Add something you want to own and it shows up here.' },
  lent: { title: 'Nothing is lent out.', body: 'Lend an item from its page and it shows up here.' },
};

export function LibraryGrid({ title, fixedStatus, openAdd, hideTitle }: LibraryGridProps) {
  const { libraryId, isLoading: loadingLibrary } = useActiveLibraryId();
  const { theme } = useUnistyles();
  // Plain objects: SafeAreaView lives outside the babel-processed tree, so a
  // themed sheet value would arrive unstyled on web (#305).
  const loading = { padding: theme.spacing.lg, gap: theme.spacing.lg };
  if (loadingLibrary || !libraryId) {
    return (
      <SafeAreaView testID="library-safe-area" edges={['left', 'right']} style={loading}>
        {hideTitle ? null : (
          <Text variant="title" accessibilityRole="header">
            {title}
          </Text>
        )}
        <LoadingState label="Loading your library…" />
      </SafeAreaView>
    );
  }
  return <Grid libraryId={libraryId} title={title} fixedStatus={fixedStatus} openAdd={openAdd} hideTitle={hideTitle} />;
}

function Grid({ libraryId, title, fixedStatus, openAdd, hideTitle }: LibraryGridProps & { libraryId: string }) {
  const router = useRouter();
  const { theme } = useUnistyles();
  const safe = { flex: 1, backgroundColor: theme.colors.ground };
  const { width } = useWindowDimensions();
  const columns = width >= WIDE_BREAKPOINT ? 6 : 3;
  const tileWidth = Math.floor((width - spacing.lg * 2 - spacing.md * (columns - 1)) / columns);

  const [chosen, setState] = useState<ListState>(defaultState());
  const [search, setSearch] = useState('');
  const [panel, setPanel] = useState<Panel>(openAdd ? 'add' : null);
  /** "Needs attention" (#345): kept rows instead of the grid. Only the library view has it, not Wishlist or Lent out. */
  const [attention, setAttention] = useState(false);
  const { items: attentionItems } = useProvisionals(!fixedStatus ? libraryId : undefined);
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
    <View style={styles.header}>
      <View style={styles.headerRow}>
        {hideTitle ? null : (
          <Text variant="title" accessibilityRole="header" style={styles.title}>
            {title}
          </Text>
        )}
        <Button label="Add item" variant="secondary" onPress={() => setPanel(panel === 'add' ? null : 'add')} />
      </View>

      {panel === 'add' ? <AddItemPanel libraryId={libraryId} onClose={() => setPanel(null)} /> : null}

      <TextField
        label="Search"
        value={search}
        onChangeText={setSearch}
        placeholder={searchPlaceholder(counts?.total)}
        returnKeyType="search"
        autoCapitalize="none"
        autoCorrect={false}
      />

      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
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

      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
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
        <Chip
          label="Edition unverified"
          selected={!!filters.unverifiedOnly}
          onPress={() => filter({ unverifiedOnly: filters.unverifiedOnly ? undefined : true })}
        />
        <Chip label={`Sort: ${sortLabel(filters.sort)}`} accessibilityLabel="Sort" onPress={() => setPanel('sort')} />
        {!fixedStatus ? (
          <Chip
            label={attentionItems.length > 0 ? `Needs attention ${attentionItems.length}` : 'Needs attention'}
            accessibilityLabel="Needs attention"
            selected={attention}
            onPress={() => setAttention((a) => !a)}
          />
        ) : null}
      </ScrollView>

      {panel === 'format' ? (
        <Sheet title="Format" onClose={() => setPanel(null)}>
          <View style={styles.wrap}>
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
          <View style={styles.wrap}>
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
          <View style={styles.wrap}>
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
    <LoadingState label="Loading your library…" />
  ) : hasActiveFilter(filters) ? (
    <EmptyState
      title="Nothing matches."
      body="Try a different search or filter."
      actionLabel="Clear filters"
      onAction={clearFilters}
      animation="no-results"
    />
  ) : (
    <EmptyState
      title={emptyCopy[view].title}
      body={emptyCopy[view].body}
      actionLabel={view === 'library' ? 'Scan your first shelf' : undefined}
      onAction={view === 'library' ? () => router.push('/scan') : undefined}
      animation="empty-library"
    />
  );

  if (attention) {
    return (
      <SafeAreaView testID="library-safe-area" edges={['left', 'right']} style={safe}>
        <ScrollView testID="needs-attention" contentContainerStyle={styles.listContent}>
          <View style={styles.header}>
            <View style={styles.headerRow}>
              {hideTitle ? null : (
                <Text variant="title" accessibilityRole="header" style={styles.title}>
                  {title}
                </Text>
              )}
            </View>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
              <Chip
                label={attentionItems.length > 0 ? `Needs attention ${attentionItems.length}` : 'Needs attention'}
                accessibilityLabel="Needs attention"
                selected
                onPress={() => setAttention(false)}
              />
            </ScrollView>
          </View>
          <NeedsAttention libraryId={libraryId} />
        </ScrollView>
        <View style={styles.snackbarPad}>
          <UndoSnackbar />
        </View>
      </SafeAreaView>
    );
  }

  // The tab layout's safe area and the tab bar already account for the top and bottom
  // insets; the sides keep landscape content clear of the notch.
  return (
    <SafeAreaView testID="library-safe-area" edges={['left', 'right']} style={safe}>
      <FlatList<ItemSummaryDto>
        key={columns}
        testID="library-grid"
        data={list.items}
        numColumns={columns}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.listContent}
        columnWrapperStyle={styles.column}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        refreshing={list.isRefreshing}
        onRefresh={() => list.refetch()}
        onEndReachedThreshold={0.5}
        onEndReached={() => {
          if (list.hasNextPage && !list.isFetchingNextPage) list.fetchNextPage();
        }}
        ListFooterComponent={list.isFetchingNextPage ? <Text variant="caption" tone="muted">Loading more…</Text> : null}
        renderItem={({ item, index }) => (
          <ItemTile item={item} width={tileWidth} index={index} onPress={() => router.push(`/item/${item.id}`)} />
        )}
      />
      <View style={styles.snackbarPad}>
        <UndoSnackbar />
      </View>
    </SafeAreaView>
  );
}

function ItemTile({ item, width, index, onPress }: { item: ItemSummaryDto; width: number; index: number; onPress: () => void }) {
  const details = [formatLabels[item.format], item.platform].filter(Boolean).join(', ');
  const reduceMotion = useReduceMotion();
  return (
    <Animated.View
      entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.quick).delay(staggerDelay(index)))}
      layout={maybeLayout(reduceMotion)}
      style={styles.tile(width)}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={item.editionVerified ? `${item.title}, ${details}` : `${item.title}, ${details}, edition unverified`}
        onPress={onPress}
        testID={`item-${item.id}`}
        style={styles.tilePressable}
      >
        <Cover title={item.title} uri={item.coverUrl} width={width} sharedTag={sharedCoverTag(reduceMotion, item.id)} />
        <Text variant="label" numberOfLines={2}>
          {item.title}
        </Text>
        <View style={styles.badges}>
          <FormatBadge label={formatLabels[item.format]} />
          {item.editionVerified ? null : <FormatBadge label="Edition unverified" />}
        </View>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  listContent: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  column: { gap: theme.spacing.md },
  header: { gap: theme.spacing.md, paddingBottom: theme.spacing.md },
  headerRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'flex-end', gap: theme.spacing.md },
  title: { flex: 1 },
  chipRow: { gap: theme.spacing.sm },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  snackbarPad: { padding: theme.spacing.lg },
  tilePressable: { gap: theme.spacing.xs },
  badges: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.xs },
  tile: (width: number) => ({ width }),
}));
