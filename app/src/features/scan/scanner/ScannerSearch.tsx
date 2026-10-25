// Typed search from the scanner: the catalog scoped to this library (owned answers first), the owned copy when offline.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useSearchCatalog } from '@/api/generated/catalog/catalog';
import type { ReleaseDto, ReleaseFormat } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { useDebounced } from '@/features/library/useItems';
import { staleCopyMessage } from '@/features/offline/copy';
import { useOnline } from '@/features/offline/hooks';
import { getOwnedCache } from '@/features/offline/ownedCache';
import type { OwnedCopy } from '@/features/offline/ownedIndex';
import { ListRow, Sheet, Text, TextField } from '@/ui';

const formatName = (format: string | null): string | null =>
  format && format in formatLabels ? formatLabels[format as ReleaseFormat] : format;

const ownedLine = (copies: OwnedCopy[]): string | null => {
  if (copies.length === 0) return null;
  const first = copies.find((c) => c.locationPath)?.locationPath;
  return `You own ${copies.length}${first ? ` · ${first}` : ''}`;
};

export function ScannerSearch({ libraryId, onClose }: { libraryId: string; onClose(): void }) {
  const [text, setText] = useState('');
  const [now] = useState(() => Date.now()); // the stale line is computed once per opening, not per keystroke
  const q = useDebounced(text.trim());
  const online = useOnline();
  const cache = getOwnedCache(libraryId);
  const search = useSearchCatalog({ q, scope: 'library' }, { query: { enabled: online && q.length >= 2 } });
  const results = search.data?.status === 200 ? search.data.data.items : [];
  const asked = q.length >= 2;
  // Offline, or the server failed: answer from the owned copy instead of failing.
  const local = asked && (!online || search.isError) ? cache.state.index.searchTitles(q) : [];
  const hits = results.flatMap((r) => r.releases).length + local.length;

  return (
    <Sheet title="Search" onClose={onClose}>
      <View style={styles.body}>
        <TextField label="Type to search" value={text} onChangeText={setText} placeholder="Title, author or barcode" autoCapitalize="none" autoCorrect={false} />
        {!online ? (
          <Text variant="caption" tone="muted">
            {staleCopyMessage(cache.state.syncedAt, now)}
          </Text>
        ) : null}
        {search.isError ? (
          <Text tone="danger" accessibilityRole="alert">
            {"Couldn't search the catalog. Check your connection."}
          </Text>
        ) : null}
        {asked && !search.isLoading && !search.isError && hits === 0 ? (
          <Text variant="caption" tone="muted">
            No matches yet.
          </Text>
        ) : null}
        {results.flatMap((result) =>
          result.releases.map((release: ReleaseDto) => {
            const detail = [formatName(release.format), release.platform?.name ?? null, release.year ?? null, ownedLine(cache.state.index.lookup({ releaseId: release.id }).owned)]
              .filter((part) => part !== null && part !== '')
              .join(' · ');
            return <ListRow key={release.id} title={release.title} subtitle={detail} />;
          }),
        )}
        {local.map((copy) => {
          const detail = [formatName(copy.format), copy.platform, copy.locationPath].filter((part) => part !== null && part !== '').join(' · ');
          return <ListRow key={copy.itemId} title={copy.title ?? 'Unknown item'} subtitle={detail} />;
        })}
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  body: { gap: theme.spacing.sm },
}));
