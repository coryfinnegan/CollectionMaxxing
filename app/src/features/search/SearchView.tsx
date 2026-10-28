// The dedicated Search view (#458): as-you-type catalog search with kind,
// platform and year filters, recent searches, one row per work, and each
// work's editions one tap down. AddItemPanel hosts it; ScannerSearch adopts
// it in #459 with scope=library and the owned cache as its offline state.
// Owned state comes from each result's ownedReleaseIds (#456).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useState } from 'react';
import { ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useListPlatforms, useSearchCatalog } from '@/api/generated/catalog/catalog';
import type { CatalogSearchResultDto, PlatformDto, ReleaseFormat, WorkKind } from '@/api/generated/models';
import { formatLabels, kindLabels, kinds } from '@/features/library/labels';
import { useDebounced } from '@/features/library/useItems';
import { useOnline } from '@/features/offline/hooks';
import type { OwnedCopy } from '@/features/offline/ownedIndex';
import { friendlyError } from '@/lib/problem';
import { Button, Chip, Cover, EmptyState, ListRow, LoadingState, Text, TextField } from '@/ui';
import { useRecentSearches } from './recent';
import { EditionRow, representativeCoverOf, WorkRow, yearSpanOf } from './rows';

/** The catalog answer the view renders: the live query, or a pinned one for fixtures. */
export type SearchAnswer = {
  items: CatalogSearchResultDto[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  refetch(): void;
};

/** The owned-cache answers the view renders while offline, per the ScannerSearch pattern (#459). */
export type OfflineSearchSource = {
  /** Owned copies matching the query, best match first. */
  searchTitles(query: string): OwnedCopy[];
  /** The stale-copy line ("Based on your last sync, 2 h ago"), computed once per opening. */
  staleLine: string;
};

export type SearchViewProps = {
  onPick(releaseId: string): void;
  /** Absent when the host has no manual-entry path (the scanner): no-matches then offers no action. */
  onManual?(): void;
  initialQuery?: string;
  /** The scanner searches its library first (owned answers first); the library search leaves this unset. */
  scope?: 'library';
  /** The offline state: owned answers with the stale-copy line. Without it, offline is the "You're offline" line. */
  offline?: OfflineSearchSource;
  /** Fixtures pin the catalog answer (loading, results, empty, error) without touching the network. */
  search?: SearchAnswer;
};

const asked = (q: string) => q.length >= 2;
const formatName = (format: string | null): string | null =>
  format && format in formatLabels ? formatLabels[format as ReleaseFormat] : format;
const parseYear = (text: string): number | undefined => {
  const trimmed = text.trim();
  return /^\d{4}$/.test(trimmed) ? Number(trimmed) : undefined;
};

export function SearchView({ onPick, onManual, initialQuery = '', scope, offline: offlineSource, search: pinned }: SearchViewProps) {
  const [text, setText] = useState(initialQuery);
  const [kind, setKind] = useState<WorkKind | null>(null);
  const [platform, setPlatform] = useState<PlatformDto | null>(null);
  const [yearText, setYearText] = useState('');
  const [selectedWorkId, setSelectedWorkId] = useState<string | null>(null);
  const q = useDebounced(text.trim());
  const year = parseYear(yearText);
  const online = useOnline();
  const { recent, save, clear } = useRecentSearches();

  const live = pinned === undefined;
  const catalog = useSearchCatalog(
    { q, kind: kind ?? undefined, platform: platform?.name, year, scope },
    { query: { enabled: live && online && asked(q) } },
  );
  const platforms = useListPlatforms({ query: { enabled: live && kind === 'game' } });
  const platformItems = platforms.data?.status === 200 ? platforms.data.data.items : [];

  const answer: SearchAnswer = pinned ?? {
    items: catalog.data?.status === 200 ? catalog.data.data.items : [],
    isLoading: catalog.isPending || catalog.isFetching,
    isError: catalog.isError,
    error: catalog.error,
    refetch: () => void catalog.refetch(),
  };
  const { items } = answer;

  // Successful searches join the recents; dead ends and failures leave no trace.
  useEffect(() => {
    if (live && asked(q) && !answer.isLoading && !answer.isError && items.length > 0) save(q);
  }, [live, q, answer.isLoading, answer.isError, items, save]);

  const selected = selectedWorkId ? (items.find((r) => r.work.id === selectedWorkId) ?? null) : null;
  if (selected) return <Editions result={selected} onBack={() => setSelectedWorkId(null)} onPick={onPick} />;

  const pickKind = (next: WorkKind | null) => {
    setKind(next);
    if (next !== 'game') setPlatform(null);
  };

  return (
    <View style={styles.step}>
      <TextField
        label="Search the catalog"
        value={text}
        onChangeText={setText}
        placeholder="Title, author or barcode"
        autoCapitalize="none"
        autoCorrect={false}
        returnKeyType="search"
      />
      <Text variant="label">Kind</Text>
      <View style={styles.wrap}>
        <Chip label="All" accessibilityLabel="All kinds" selected={kind === null} onPress={() => pickKind(null)} />
        {kinds.map((k) => (
          <Chip key={k} label={kindLabels[k]} accessibilityLabel={`Kind ${kindLabels[k]}`} selected={kind === k} onPress={() => pickKind(k)} />
        ))}
      </View>
      {kind === 'game' && platformItems.length > 0 ? (
        <>
          <Text variant="label">Platform</Text>
          <ScrollView horizontal keyboardShouldPersistTaps="handled" contentContainerStyle={styles.platforms}>
            <Chip label="All" accessibilityLabel="All platforms" selected={platform === null} onPress={() => setPlatform(null)} />
            {platformItems.map((p) => (
              <Chip
                key={p.id}
                label={p.shortName}
                accessibilityLabel={`Platform ${p.shortName}`}
                selected={platform?.id === p.id}
                onPress={() => setPlatform(p)}
              />
            ))}
          </ScrollView>
        </>
      ) : null}
      <TextField label="Year" value={yearText} onChangeText={setYearText} placeholder="1999" keyboardType="number-pad" maxLength={4} />
      <SearchBody
        q={q}
        answer={answer}
        offline={live && !online}
        offlineSource={offlineSource}
        recent={recent}
        onRecent={setText}
        onClearRecent={clear}
        onManual={onManual}
        onOpen={(result) => setSelectedWorkId(result.work.id)}
      />
    </View>
  );
}

function SearchBody({
  q,
  answer,
  offline,
  offlineSource,
  recent,
  onRecent,
  onClearRecent,
  onManual,
  onOpen,
}: {
  q: string;
  answer: SearchAnswer;
  offline: boolean;
  offlineSource?: OfflineSearchSource;
  recent: string[];
  onRecent(term: string): void;
  onClearRecent(): void;
  onManual?(): void;
  onOpen(result: CatalogSearchResultDto): void;
}) {
  if (!asked(q)) {
    if (recent.length === 0) {
      return (
        <Text variant="caption" tone="muted">
          Type at least two characters to search.
        </Text>
      );
    }
    return (
      <View style={styles.step}>
        <Text variant="label">Recent searches</Text>
        <View style={styles.wrap}>
          {recent.map((term) => (
            <Chip key={term} label={term} accessibilityLabel={`Search again for ${term}`} onPress={() => onRecent(term)} />
          ))}
        </View>
        <Button label="Clear recent searches" variant="secondary" onPress={onClearRecent} />
      </View>
    );
  }
  if (offline && offlineSource) {
    const copies = offlineSource.searchTitles(q);
    return (
      <View style={styles.step}>
        <Text variant="caption" tone="muted">
          {offlineSource.staleLine}
        </Text>
        {copies.length > 0 ? <OwnedRows copies={copies} /> : <NoMatches onManual={onManual} />}
      </View>
    );
  }
  if (offline) {
    return (
      <EmptyState title="You're offline" body="Check your connection and try again." actionLabel="Try again" onAction={answer.refetch} />
    );
  }
  if (answer.isLoading) return <LoadingState label="Searching the catalog" />;
  if (answer.isError) {
    if (offlineSource) {
      // The server failed but the owned copy answers: the error line plus what you own.
      const copies = offlineSource.searchTitles(q);
      if (copies.length > 0) {
        return (
          <View style={styles.step}>
            <Text tone="danger" accessibilityRole="alert">
              {"Couldn't search the catalog. Check your connection."}
            </Text>
            <OwnedRows copies={copies} />
          </View>
        );
      }
    }
    return <EmptyState title="Couldn't search the catalog" body={friendlyError(answer.error)} actionLabel="Try again" onAction={answer.refetch} />;
  }
  if (answer.items.length === 0) {
    return <NoMatches onManual={onManual} />;
  }
  return (
    <View style={styles.rows}>
      {answer.items.map((result, i) => (
        // Provider works share the empty id, so the index keeps keys apart.
        <WorkRow key={`${result.work.id}-${i}`} result={result} ownedReleaseIds={new Set(result.ownedReleaseIds)} onPress={() => onOpen(result)} />
      ))}
    </View>
  );
}

function NoMatches({ onManual }: { onManual?(): void }) {
  return (
    <EmptyState
      title="No matches"
      body="Try different words or fewer filters."
      actionLabel={onManual ? 'Add it yourself' : undefined}
      onAction={onManual}
      animation="no-results"
    />
  );
}

function OwnedRows({ copies }: { copies: OwnedCopy[] }) {
  return (
    <View style={styles.rows}>
      {copies.map((copy) => {
        const detail = [formatName(copy.format), copy.platform, copy.locationPath].filter((part) => part !== null && part !== '').join(' · ');
        return <ListRow key={copy.itemId} title={copy.title ?? 'Unknown item'} subtitle={detail} />;
      })}
    </View>
  );
}

function Editions({
  result,
  onBack,
  onPick,
}: {
  result: CatalogSearchResultDto;
  onBack(): void;
  onPick(releaseId: string): void;
}) {
  const owned = new Set(result.ownedReleaseIds);
  const { work, releases } = result;
  const creators = work.creators.length > 0 ? work.creators : (releases[0]?.creators ?? []);
  const span = yearSpanOf(releases) || (work.originalYear ? String(work.originalYear) : '');
  const facts = [creators.length > 0 ? creators.join(', ') : '', span, `${releases.length} ${releases.length === 1 ? 'edition' : 'editions'}`]
    .filter(Boolean)
    .join(' · ');
  const uri = representativeCoverOf(result, owned);
  return (
    <View style={styles.step}>
      <Button label="Back to results" variant="secondary" onPress={onBack} />
      <View style={styles.header}>
        {uri ? (
          <Cover title={work.title} uri={uri} width={72} />
        ) : (
          // No art anywhere: a plain tile, never a text stand-in for the cover (#440).
          <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.tile} />
        )}
        <View style={styles.headerText}>
          <Text variant="heading" accessibilityRole="header">
            {work.title}
          </Text>
          {facts ? (
            <Text variant="mono" tone="muted">
              {facts}
            </Text>
          ) : null}
        </View>
      </View>
      <View style={styles.rows}>
        {releases.map((release, i) => (
          // Provider releases share the empty id, so the index keeps keys apart.
          <EditionRow key={`${release.id}-${i}`} release={release} ownedCount={owned.has(release.id) ? 1 : 0} onPress={() => onPick(release.id)} />
        ))}
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  step: { gap: theme.spacing.md },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  platforms: { gap: theme.spacing.sm },
  rows: { gap: theme.spacing.sm },
  header: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  headerText: { flex: 1, gap: theme.spacing.xs },
  tile: {
    width: 72,
    height: 108,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
  },
}));
