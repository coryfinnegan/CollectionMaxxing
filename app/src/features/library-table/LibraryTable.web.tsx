// The web library table: sortable columns, row selection, bulk edit, column chooser and inline edit.
// Web only. Wishlist and Lent out are the same table with a fixed status.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { Pressable, ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { bulkItems, getStats } from '@/api/generated/items/items';
import { listTags } from '@/api/generated/tags/tags';
import type { BulkItemsRequest, ItemStatus, ItemSummaryDto } from '@/api/generated/models';
import { UndoSnackbar } from '@/features/item/UndoSnackbar';
import { useInvalidateLibrary } from '@/features/item/useItem';
import { formatLabels, formats, kindLabels, kinds, statusLabels, statuses } from '@/features/library/labels';
import { changeFilter, defaultState, hasActiveFilter, type ListState } from '@/features/library/query';
import { useDebounced, useItems } from '@/features/library/useItems';
import { flattenTree } from '@/features/locations/tree';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { LibraryLoading } from '@/features/onboarding/LibraryLoading';
import { NeedsAttention, useProvisionals } from '@/features/library/NeedsAttention';
import { localDateString } from '@/lib/dates';
import { friendlyError } from '@/lib/problem';
import { Button, Chip, Cover, EmptyState, FormatBadge, ListRow, LoadingState, Sheet, Text, TextField } from '@/ui';
import { capForBulk, resultMessage } from './bulk';
import { ColumnChooser } from './ColumnChooser.web';
import { columnDefs, type ColumnId, loadColumns, saveColumns, sortForColumn } from './columns';
import { onGridKeyDown } from './gridKeys.web';
import { InlineCell } from './InlineCell.web';
import { clearSelection, clickRow, emptySelection, selectedInOrder, type Selection, togglePage } from './selection';
import { SelectionBar } from './SelectionBar.web';

export type LibraryTableProps = { title: string; fixedStatus?: ItemStatus };

type Panel = 'columns' | 'format' | 'platform' | 'location' | null;

const CHECK_WIDTH = 44;

/** Reads the modifier keys off a press event (react-native-web passes the DOM event through). */
function modifiers(event: unknown) {
  const e = event as { shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; nativeEvent?: { shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean } } | undefined;
  const source = { ...e?.nativeEvent, ...e };
  return { shift: !!source.shiftKey, toggle: !!(source.ctrlKey || source.metaKey) };
}

/** react-native-web passes keyboard events to View; React Native's own types do not list the prop. */
const gridKeys = { onKeyDown: onGridKeyDown } as object;

export function LibraryTable({ title, fixedStatus }: LibraryTableProps) {
  const { libraryId, isLoading } = useActiveLibraryId();
  if (isLoading || !libraryId) {
    return (
      <View style={styles.loading}>
        <Text variant="title" accessibilityRole="header">
          {title}
        </Text>
        <LibraryLoading label="Loading your library…" />
      </View>
    );
  }
  return <Table libraryId={libraryId} title={title} fixedStatus={fixedStatus} />;
}

function Table({ libraryId, title, fixedStatus }: LibraryTableProps & { libraryId: string }) {
  const router = useRouter();
  const invalidate = useInvalidateLibrary();
  const queryClient = useQueryClient();

  const [chosen, setState] = useState<ListState>(defaultState());
  const [search, setSearch] = useState('');
  const [panel, setPanel] = useState<Panel>(null);
  const [columns, setColumns] = useState<ColumnId[]>(loadColumns);
  const [selection, setSelection] = useState<Selection>(emptySelection());
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  /** "Needs attention" (#345): kept rows instead of the table. Only the library view has it, not Wishlist or Lent out. */
  const [attention, setAttention] = useState(false);
  const { items: attentionItems } = useProvisionals(!fixedStatus ? libraryId : undefined);
  const debouncedSearch = useDebounced(search);
  const state = changeFilter(chosen, { q: debouncedSearch });
  const { filters } = state;

  const list = useItems(libraryId, state, fixedStatus);
  const { locations } = useLocationsList(libraryId);
  const tags = useQuery({
    queryKey: ['library-tags', libraryId],
    queryFn: async () => (await listTags(libraryId)).data as { items: { id: string; name: string; itemCount: number }[] },
  });
  const rowIds = list.items.map((i) => i.id);
  const selectedIds = selectedInOrder(selection, rowIds);
  const capped = capForBulk(selectedIds);
  const locationName = (id: string | null) => locations.find((l) => l.id === id)?.name;
  const visible = columnDefs.filter((c) => columns.includes(c.id));
  const tableWidth = CHECK_WIDTH + visible.reduce((sum, c) => sum + c.width, 0);

  const filter = (patch: Partial<typeof filters>) => {
    setState((current) => changeFilter(current, patch));
    setSelection(clearSelection());
  };
  const closeAnd = (patch: Partial<typeof filters>) => {
    filter(patch);
    setPanel(null);
  };
  const updateColumns = (next: ColumnId[]) => {
    setColumns(next);
    saveColumns(next);
  };

  const runBulk = async (verb: string, body: Omit<BulkItemsRequest, 'ids'>) => {
    const ids = capped.ids;
    setBusy(true);
    setNotice(null);
    try {
      const response = await bulkItems(libraryId, { ...body, ids });
      const affected = response.status === 200 ? response.data.affected : 0;
      const message = resultMessage(verb, ids.length, affected);
      setNotice(message);
      if (message.ok) setSelection(clearSelection());
    } catch (error) {
      setNotice({ ok: false, text: friendlyError(error) });
    } finally {
      setBusy(false);
      await invalidate(libraryId);
      await queryClient.invalidateQueries({ queryKey: ['library-tags', libraryId] });
    }
  };

  const sortBy = (id: ColumnId) => {
    const sort = sortForColumn(id);
    if (sort) filter({ sort });
  };

  const locationOptions = [
    { value: '', label: 'No location' },
    ...flattenTree(locations).map(({ location, depth }) => ({ value: location.id, label: location.name, depth })),
  ];
  const statusOptions = statuses.map((s) => ({ value: s, label: statusLabels[s] }));

  const cell = (item: ItemSummaryDto, id: ColumnId) => {
    switch (id) {
      case 'cover':
        return <Cover title={item.title} uri={item.coverUrl} width={40} />;
      case 'title':
        return (
          <View style={styles.titleCell}>
            <Pressable
              accessibilityRole="link"
              accessibilityLabel={item.editionVerified ? `Open ${item.title}` : `Open ${item.title}, edition unverified`}
              onPress={() => router.push(`/item/${item.id}`)}
              style={styles.cellPress}
            >
              <Text variant="label" numberOfLines={2}>
                {item.title}
              </Text>
            </Pressable>
            {item.editionVerified ? null : <FormatBadge label="Edition unverified" />}
          </View>
        );
      case 'format':
        return <Text>{formatLabels[item.format]}</Text>;
      case 'platform':
        return <Text tone={item.platform ? undefined : 'muted'}>{item.platform ?? '—'}</Text>;
      case 'year':
        return <Text tone={item.year ? undefined : 'muted'}>{item.year ?? '—'}</Text>;
      case 'location':
        return (
          <InlineCell
            libraryId={libraryId}
            item={item}
            field="location"
            current={locationName(item.locationId) ?? 'No location'}
            options={locationOptions}
            onNotice={(text) => setNotice(text ? { ok: false, text } : null)}
          />
        );
      case 'status':
        return (
          <InlineCell
            libraryId={libraryId}
            item={item}
            field="status"
            current={statusLabels[item.status]}
            options={statusOptions}
            onNotice={(text) => setNotice(text ? { ok: false, text } : null)}
          />
        );
      case 'added':
        return <Text tone="muted">{localDateString(item.createdAt)}</Text>;
    }
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
      onAction={() => {
        setSearch('');
        setState(defaultState());
      }}
      animation="no-results"
    />
  ) : (
    <EmptyState title="Nothing here yet." body="Use Add item to put something on your shelf." animation="empty-library" />
  );

  const allSelected = rowIds.length > 0 && rowIds.every((id) => selection.ids.has(id));
  const someSelected = selectedIds.length > 0;

  return (
    <View style={styles.root}>
      <ScrollView contentContainerStyle={styles.page}>
        <Text variant="title" accessibilityRole="header">
          {title}
        </Text>

        <TextField
          label="Search"
          value={search}
          onChangeText={setSearch}
          placeholder="Search your library"
          returnKeyType="search"
          autoCapitalize="none"
          autoCorrect={false}
        />

        <View style={styles.chipWrap}>
          <Chip label="All kinds" selected={!filters.kind} onPress={() => filter({ kind: undefined })} />
          {kinds.map((kind) => (
            <Chip
              key={kind}
              label={kindLabels[kind]}
              selected={filters.kind === kind}
              onPress={() => filter({ kind: filters.kind === kind ? undefined : kind })}
            />
          ))}
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
            label={locationName(filters.locationId ?? null) ?? 'Any location'}
            accessibilityLabel="Filter by location"
            selected={!!filters.locationId}
            onPress={() => setPanel('location')}
          />
          <Chip
            label="Edition unverified"
            selected={!!filters.unverifiedOnly}
            onPress={() => filter({ unverifiedOnly: filters.unverifiedOnly ? undefined : true })}
          />
          <Chip label="Columns" accessibilityLabel="Choose columns" onPress={() => setPanel(panel === 'columns' ? null : 'columns')} />
          {!fixedStatus ? (
            <Chip
              label={attentionItems.length > 0 ? `Needs attention ${attentionItems.length}` : 'Needs attention'}
              accessibilityLabel="Needs attention"
              selected={attention}
              onPress={() => setAttention((a) => !a)}
            />
          ) : null}
        </View>

        {panel === 'columns' ? <ColumnChooser columns={columns} onChange={updateColumns} onClose={() => setPanel(null)} /> : null}

        {panel === 'format' ? (
          <Sheet title="Format" onClose={() => setPanel(null)}>
            <View style={styles.wrap}>
              <Chip label="Any format" selected={!filters.format} onPress={() => closeAnd({ format: undefined })} />
              {formats.map((format) => (
                <Chip key={format} label={formatLabels[format]} selected={filters.format === format} onPress={() => closeAnd({ format })} />
              ))}
            </View>
          </Sheet>
        ) : null}

        {panel === 'platform' ? (
          <PlatformSheet
            libraryId={libraryId}
            selected={filters.platform}
            onPick={(platform) => closeAnd({ platform })}
            onClose={() => setPanel(null)}
          />
        ) : null}

        {panel === 'location' ? (
          <Sheet title="Location" onClose={() => setPanel(null)}>
            <ListRow title="Any location" onPress={() => closeAnd({ locationId: undefined })} />
            {flattenTree(locations).map(({ location, depth }) => (
              <ListRow key={location.id} title={location.name} depth={depth} onPress={() => closeAnd({ locationId: location.id })} />
            ))}
          </Sheet>
        ) : null}

        {notice ? (
          <Text accessibilityRole="alert" tone={notice.ok ? 'ink' : 'danger'}>
            {notice.text}
          </Text>
        ) : null}

        {attention ? (
          <NeedsAttention libraryId={libraryId} />
        ) : (
          <>
        <ScrollView horizontal testID="library-table-scroll" style={styles.tableScroll}>
          <View role="grid" accessibilityLabel={`${title} table`} {...gridKeys} style={styles.grid(tableWidth)}>
            <View role="row" style={styles.headerRow}>
              <View role="cell" style={styles.checkCell}>
                <Checkbox
                  label="Select all loaded items"
                  checked={allSelected}
                  mixed={someSelected && !allSelected}
                  onPress={() => setSelection((s) => togglePage(s, rowIds))}
                />
              </View>
              {visible.map((column) => {
                const sort = sortForColumn(column.id);
                const active = sort !== undefined && sort === filters.sort;
                return (
                  <View key={column.id} role="columnheader" style={styles.columnHeader(column.width)}>
                    {sort ? (
                      <Pressable
                        accessibilityRole="button"
                        accessibilityLabel={`Sort by ${column.label}`}
                        style={styles.cellPress}
                        accessibilityState={{ selected: active }}
                        onPress={() => sortBy(column.id)}
                      >
                        <Text variant="label" tone={active ? 'accent' : 'ink'}>
                          {column.label}
                          {active ? ' ↑' : ''}
                        </Text>
                      </Pressable>
                    ) : (
                      <Text variant="label" tone="muted">
                        {column.label}
                      </Text>
                    )}
                  </View>
                );
              })}
            </View>

            {list.items.map((item) => {
              const selected = selection.ids.has(item.id);
              return (
                <Pressable
                  key={item.id}
                  role="row"
                  accessibilityState={{ selected }}
                  testID={`row-${item.id}`}
                  // The checkbox is the keyboard stop for a row; the row itself is for mouse clicks.
                  focusable={false}
                  onPress={(event) => setSelection((s) => clickRow(s, rowIds, item.id, modifiers(event)))}
                  style={styles.row(selected)}
                >
                  <View role="cell" style={styles.checkCell}>
                    <Checkbox
                      label={`Select ${item.title}`}
                      checked={selected}
                      onPress={(event) => setSelection((s) => clickRow(s, rowIds, item.id, { ...modifiers(event), toggle: !modifiers(event).shift }))}
                    />
                  </View>
                  {visible.map((column) => (
                    <View key={column.id} role="cell" style={styles.cell(column.width)}>
                      {cell(item, column.id)}
                    </View>
                  ))}
                </Pressable>
              );
            })}
          </View>
        </ScrollView>

        {/* Empty, loading and error states sit outside the horizontal scroller, so they are centered on the visible width. */}
        {list.items.length === 0 ? <View style={styles.emptyWrap}>{empty}</View> : null}

        {list.hasNextPage ? (
          <View style={styles.loadMore}>
            <Button
              label="Load more"
              variant="secondary"
              busy={list.isFetchingNextPage}
              onPress={() => list.fetchNextPage()}
            />
          </View>
        ) : null}

        {someSelected ? (
          <SelectionBar
            count={capped.ids.length}
            selectedCount={selectedIds.length}
            capMessage={capped.message}
            busy={busy}
            locations={locations}
            tags={tags.data?.items ?? []}
            onMove={(locationId) => runBulk('Moved', { action: 'patch', patch: { locationId } })}
            onStatus={(status) => runBulk('Updated', { action: 'patch', patch: { status } })}
            onTag={(tagId) => runBulk('Tagged', { action: 'patch', patch: { addTagIds: [tagId] } })}
            onDelete={() => runBulk('Deleted', { action: 'delete' })}
            onKeepFirst={() => setSelection({ ids: new Set(capped.ids), anchor: selection.anchor })}
            onClear={() => setSelection(clearSelection())}
          />
        ) : null}
          </>
        )}
      </ScrollView>
      <View style={styles.snackbarPad}>
        <UndoSnackbar />
      </View>
    </View>
  );
}

function Checkbox({
  label,
  checked,
  mixed,
  onPress,
}: {
  label: string;
  checked: boolean;
  mixed?: boolean;
  onPress: (event: unknown) => void;
}) {
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityLabel={label}
      aria-checked={mixed ? 'mixed' : checked}
      // Space toggles a checkbox from the keyboard.
      {...({
        onKeyDown: (event: { key: string; preventDefault?: () => void }) => {
          if (event.key !== ' ') return;
          event.preventDefault?.();
          onPress(event);
        },
      } as object)}
      onPress={(event) => {
        // Keep the click from also selecting the row underneath.
        (event as { stopPropagation?: () => void })?.stopPropagation?.();
        onPress(event);
      }}
      // The pressable is the 44 px touch target; the box inside is what you see.
      style={styles.checkboxPress}
    >
      <View style={styles.checkboxBox(checked || !!mixed)}>
        <Text variant="caption" style={styles.checkGlyph}>
          {checked ? '✓' : mixed ? '–' : ''}
        </Text>
      </View>
    </Pressable>
  );
}

function PlatformSheet({
  libraryId,
  selected,
  onPick,
  onClose,
}: {
  libraryId: string;
  selected?: string;
  onPick: (platform: string | undefined) => void;
  onClose: () => void;
}) {
  // Platforms come from the library's stats, as in the grid.
  const stats = useQuery({
    queryKey: ['library-platforms', libraryId],
    queryFn: async () => {
      const response = await getStats(libraryId);
      return response.status === 200 ? Object.keys(response.data.byPlatform) : [];
    },
  });
  return (
    <Sheet title="Platform" onClose={onClose}>
      <View style={styles.wrap}>
        <Chip label="Any platform" selected={!selected} onPress={() => onPick(undefined)} />
        {(stats.data ?? []).map((platform) => (
          <Chip key={platform} label={platform} selected={selected === platform} onPress={() => onPick(platform)} />
        ))}
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  loading: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  root: { flex: 1, backgroundColor: theme.colors.ground },
  page: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  chipWrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  cellPress: { minHeight: 44, justifyContent: 'center' },
  titleCell: { gap: 2, justifyContent: 'center' },
  tableScroll: { borderRadius: theme.radius.md, borderWidth: 1, borderColor: theme.colors.line },
  grid: (tableWidth: number) => ({ width: tableWidth, backgroundColor: theme.colors.surface }),
  headerRow: { flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderColor: theme.colors.line },
  checkCell: { width: CHECK_WIDTH, alignItems: 'center' },
  columnHeader: (width: number) => ({ width, paddingHorizontal: theme.spacing.sm, justifyContent: 'center' }),
  row: (selected: boolean) => ({
    flexDirection: 'row',
    alignItems: 'center',
    borderBottomWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: selected ? theme.colors.ground : theme.colors.surface,
  }),
  cell: (width: number) => ({ width, padding: theme.spacing.sm }),
  emptyWrap: { padding: theme.spacing.lg, alignItems: 'center' },
  loadMore: { flexDirection: 'row' },
  snackbarPad: { padding: theme.spacing.lg },
  checkboxPress: { width: CHECK_WIDTH, height: CHECK_WIDTH, alignItems: 'center', justifyContent: 'center' },
  checkboxBox: (filled: boolean) => ({
    width: 22,
    height: 22,
    borderRadius: theme.radius.sm,
    borderWidth: 2,
    borderColor: filled ? theme.colors.accent : theme.colors.control,
    backgroundColor: filled ? theme.colors.accent : 'transparent',
    alignItems: 'center',
    justifyContent: 'center',
  }),
  checkGlyph: { color: theme.colors.onAccent },
}));
