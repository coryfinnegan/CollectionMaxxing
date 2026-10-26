// "Needs attention" (design §11): the library's kept rows that still need acting on. A guessed title in quotes
// (never asserted), its crop beside it, and the three ways out: add it to the library, link it to the item it is,
// or delete it. Any member may look; editors act.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { listProvisionals } from '@/api/generated/scanning/scanning';
import { ApiError } from '@/lib/http';
import type { ProvisionalDto, ScanMediaHint } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { newIdempotencyKey } from '@/features/item/etag';
import { useLocationsList } from '@/features/locations/useLocations';
import { deleteKept, promoteKept, resolveKept } from '@/features/review/provisionals';
import { useLibraryRole } from '@/features/scan/session/useScanSession';
import { ownArtworkUrl } from '@/features/scan/pipeline/artStep';
import { friendlyError } from '@/lib/problem';
import { Button, Cover, EmptyState, ListRow, LoadingState, Sheet, Text, TextField } from '@/ui';
import { changeFilter, defaultState } from './query';
import { useDebounced, useItems } from './useItems';

const MEDIA_LABEL: Record<ScanMediaHint, string | null> = { books: 'Book', games: 'Game', movies: 'Movie', mixed: null };

/** The library's open provisional records, stalest first. Shared by the Needs attention lists and their chips. */
export function useProvisionals(libraryId: string | undefined) {
  const query = useQuery({
    queryKey: ['provisionals', libraryId],
    enabled: !!libraryId,
    queryFn: async ({ signal }) => {
      const response = await listProvisionals(libraryId ?? '', { signal });
      if (response.status !== 200) throw new ApiError(response.status, {});
      return response.data.items;
    },
  });
  return { items: query.data ?? [], isLoading: query.isLoading, isError: query.isError, refetch: query.refetch };
}

export function NeedsAttention({ libraryId }: { libraryId: string }) {
  const { canAdd } = useLibraryRole(libraryId);
  const { items, isLoading, isError, refetch } = useProvisionals(libraryId);
  const { locations } = useLocationsList(libraryId);
  const locate = (id: string | null) => (id ? (breadcrumb(locations, id) ?? null) : null);

  if (isLoading) return <LoadingState label="Loading what needs attention…" />;
  if (isError) {
    return (
      <View style={styles.state}>
        <Text tone="danger">{"Couldn't load what needs attention."}</Text>
        <Button label="Try again" variant="secondary" onPress={() => void refetch()} />
      </View>
    );
  }
  if (items.length === 0) {
    return <EmptyState title="Nothing needs attention." body="Unreadable spines you keep show up here." animation="empty-library" />;
  }
  return (
    <View style={styles.list}>
      {canAdd ? null : <Text tone="muted">You can look but not act. Ask an owner or editor to work through these.</Text>}
      {items.map((p) => (
        <KeptRow key={p.id} libraryId={libraryId} provisional={p} location={locate(p.locationId)} canAct={canAdd} />
      ))}
    </View>
  );
}

function KeptRow({ libraryId, provisional: p, location, canAct }: { libraryId: string; provisional: ProvisionalDto; location: string | null; canAct: boolean }) {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState<'promote' | 'delete' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [linking, setLinking] = useState(false);
  const title = `"${p.titleGuess}"`;
  const crop = p.cropUrl ? (ownArtworkUrl(p.cropUrl) ?? null) : null;
  const caption = [MEDIA_LABEL[p.mediaHint], location].filter(Boolean).join(' · ') || 'Guessed from a scan';
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['provisionals', libraryId] }),
      queryClient.invalidateQueries({ queryKey: ['library-items', libraryId] }),
    ]);

  const promote = async () => {
    setBusy('promote');
    setError(null);
    try {
      await promoteKept(libraryId, p.id, newIdempotencyKey());
      await refresh();
    } catch (e) {
      setError(friendlyError(e, "Couldn't add that guess. It still needs attention."));
    } finally {
      setBusy(null);
    }
  };

  const remove = async () => {
    if (!confirming) {
      setConfirming(true);
      return;
    }
    setBusy('delete');
    setError(null);
    try {
      await deleteKept(libraryId, p.id);
      await refresh();
    } catch (e) {
      setError(friendlyError(e, "Couldn't delete that guess. It still needs attention."));
    } finally {
      setBusy(null);
      setConfirming(false);
    }
  };

  return (
    <View style={styles.row}>
      <View style={styles.head}>
        {crop ? <Cover title={p.titleGuess} uri={crop} width={48} /> : null}
        <View style={styles.titles}>
          <Text>{title}</Text>
          <Text variant="caption" tone="muted">
            {caption}
          </Text>
        </View>
      </View>
      {canAct ? (
        <View style={styles.actions}>
          <Button label="Add to library" accessibilityLabel={`Add ${title} to your library`} busy={busy === 'promote'} disabled={busy !== null} haptic="commit" onPress={() => void promote()} />
          <Button label="Link to item" accessibilityLabel={`Link ${title} to an item`} variant="secondary" disabled={busy !== null} onPress={() => setLinking(true)} />
          <Button
            label={confirming ? 'Confirm delete' : 'Delete'}
            accessibilityLabel={confirming ? `Confirm deleting ${title}` : `Delete ${title}`}
            variant="danger"
            busy={busy === 'delete'}
            disabled={busy !== null}
            haptic="commit"
            onPress={() => void remove()}
          />
        </View>
      ) : null}
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
      {linking ? (
        <LinkSheet
          libraryId={libraryId}
          guess={title}
          onPick={async (itemId) => {
            await resolveKept(libraryId, p.id, itemId);
            await refresh();
          }}
          onClose={() => setLinking(false)}
        />
      ) : null}
    </View>
  );
}

function LinkSheet({ libraryId, guess, onPick, onClose }: { libraryId: string; guess: string; onPick(itemId: string): Promise<void>; onClose(): void }) {
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const debounced = useDebounced(search);
  const list = useItems(libraryId, changeFilter(defaultState(), { q: debounced }));
  const pick = async (itemId: string) => {
    setBusy(itemId);
    setError(null);
    try {
      await onPick(itemId);
      onClose();
    } catch (e) {
      setError(friendlyError(e, "Couldn't link that guess. It still needs attention."));
    } finally {
      setBusy(null);
    }
  };
  return (
    <Sheet title={`Link ${guess} to…`} onClose={onClose}>
      <TextField label="Search" value={search} onChangeText={setSearch} placeholder="Search your library" returnKeyType="search" autoCapitalize="none" autoCorrect={false} />
      {list.isLoading ? <LoadingState label="Searching your library…" /> : null}
      {list.items.map((item) => (
        <ListRow
          key={item.id}
          title={item.title}
          subtitle={busy === item.id ? 'Linking…' : undefined}
          onPress={busy ? undefined : () => void pick(item.id)}
        />
      ))}
      {!list.isLoading && list.items.length === 0 ? <Text tone="muted">Nothing matches that search.</Text> : null}
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  state: { gap: theme.spacing.md, padding: theme.spacing.lg },
  list: { gap: theme.spacing.md, padding: theme.spacing.lg },
  row: { gap: theme.spacing.sm, padding: theme.spacing.md, borderRadius: theme.radius.md, borderWidth: 1, borderColor: theme.colors.line, backgroundColor: theme.colors.surface },
  head: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  titles: { flex: 1, gap: theme.spacing.xs },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
}));
