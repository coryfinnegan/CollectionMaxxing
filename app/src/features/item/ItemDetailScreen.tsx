// Item detail: cover, facts, location breadcrumb, other copies, and Edit / Move / Delete (with Undo). Beside them, the move
// that fits where the item is (I got it, Found it, Got it back, Mark as missing, Sold or gave it away): one tap, one PATCH, with Undo.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { Platform, View } from 'react-native';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import type { ItemDetailDto, UpdateItemRequest } from '@/api/generated/models';
import { completenessLabels, conditionLabels, formatLabels, kindLabels, statusLabels } from '@/features/library/labels';
import { isInCollection } from '@/features/library/statuses';
import { flattenTree } from '@/features/locations/tree';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { LibraryLoading } from '@/features/onboarding/LibraryLoading';
import { useMyRole } from '@/features/me/useMyRole';
import { boundScanStore } from '@/features/review/boundScan';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { ApiError } from '@/lib/http';
import { useGoBackOr } from '@/lib/navigation';
import { friendlyError } from '@/lib/problem';
import { Button, Card, Cover, EmptyState, FormatBadge, ListRow, LoadingState, Screen, Sheet, Snackbar, Text } from '@/ui';
import { sharedCoverTag } from '@/ui/motion';
import { breadcrumb } from './breadcrumb';
import { headerThemeFromCover } from './coverColor';
import { movesFor, patchFor, restoreOf, STATUS_MOVES, type StatusMoveName } from './statusMoves';
import { UndoSnackbar } from './UndoSnackbar';
import { useCoverAccent } from './useCoverAccent';
import { useItemDetail, useItemMutations, useUndoStore } from './useItem';

const MOVE_TOAST_MS = 10_000;

/** A move that worked: what the toast says, and the patch (with the version it produced) that takes it back. */
type DoneMove = { message: string; restore: UpdateItemRequest; etag: string | null };

export function ItemDetailScreen({ id }: { id: string }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  if (isLoading || !libraryId) {
    return (
      <Screen>
        <LibraryLoading label="Loading this item…" />
      </Screen>
    );
  }
  return <Detail libraryId={libraryId} id={id} />;
}

function Detail({ libraryId, id }: { libraryId: string; id: string }) {
  const router = useRouter();
  const reduceMotion = useReduceMotion();
  const { theme } = useUnistyles();
  const { detail, etag, isLoading, isError, error, refetch } = useItemDetail(libraryId, id);
  const { locations } = useLocationsList(libraryId);
  const { update, remove } = useItemMutations(libraryId);
  const { canEdit } = useMyRole();
  const leave = useGoBackOr('/library');
  const setDeleted = useUndoStore((s) => s.setDeleted);
  const [moving, setMoving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [done, setDone] = useState<DoneMove | null>(null);
  useEffect(() => {
    if (!done) return;
    const timer = setTimeout(() => setDone(null), MOVE_TOAST_MS);
    return () => clearTimeout(timer);
  }, [done]);
  // The header band takes its color from the cover when it can; without a cover
  // (or an unreadable one) it is the token surface, and either way the text on
  // it was contrast-checked. Detail screens only: lists never derive color.
  const accent = useCoverAccent(id, detail ? (detail.release.coverUrl ?? detail.work.coverUrl) : null);
  const header = accent ? headerThemeFromCover(accent, theme.fixed.white, theme.colors.ink) : null;

  if (isLoading) {
    return (
      <Screen>
        <LoadingState label="Loading this item…" />
      </Screen>
    );
  }
  if (isError || !detail || !etag) {
    return (
      <Screen>
        <EmptyState title="Couldn't load this item" body={friendlyError(error)} actionLabel="Try again" onAction={() => refetch()} />
        {/* The item this screen just deleted answers 404 from here on; Undo must outlive that. */}
        <UndoSnackbar />
      </Screen>
    );
  }

  const { item, work, release } = detail;
  const moves = movesFor(item.status);
  // "Also in your library" is what you have: a wishlisted or sold copy of the same work is not one of the copies.
  const otherCopies = detail.otherCopies.filter((copy) => isInCollection(copy.status));
  const copiesInLibrary = otherCopies.length + (isInCollection(item.status) ? 1 : 0);
  const title = item.customTitle ?? work.title;
  const facts = [formatLabels[release.format], release.platform?.name, release.year ?? work.originalYear, release.publisher]
    .filter(Boolean)
    .join(' · ');
  const coverUri = release.coverUrl ?? work.coverUrl;

  const move = (locationId: string | null) => {
    setMessage(null);
    update.mutate(
      { id, data: { locationId }, etag },
      {
        onSuccess: () => {
          setMoving(false);
          refetch();
        },
        onError: (e) => setMessage(friendlyError(e)),
      },
    );
  };

  // One tap, one PATCH with the version this screen loaded; the toast offers Undo, which sends the old values back.
  const runMove = (name: StatusMoveName) => {
    setMessage(null);
    setDone(null);
    const patch = patchFor(name);
    const restore = restoreOf(item, patch);
    update.mutate(
      { id, data: patch, etag },
      {
        onSuccess: (response) => setDone({ message: STATUS_MOVES[name].done, restore, etag: response.headers.get('etag') }),
        onError: (e) => setMessage(friendlyError(e)),
      },
    );
  };

  const undoMove = () => {
    if (!done?.etag) return;
    setMessage(null);
    update.mutate(
      { id, data: done.restore, etag: done.etag },
      {
        onSuccess: () => setDone(null),
        onError: (e) => {
          setDone(null);
          setMessage(e instanceof ApiError && e.status === 412 ? "Couldn't undo. It changed since, so it stays as it is." : friendlyError(e));
        },
      },
    );
  };

  const del = () => {
    setMessage(null);
    remove.mutate(
      { id, etag },
      {
        onSuccess: () => {
          setDeleted({ libraryId, id, title });
          // With no history (a fresh load) this lands on the library, where the Undo snackbar waits.
          leave();
        },
        onError: (e) => setMessage(friendlyError(e)),
      },
    );
  };

  return (
    <Screen title={title} showTitleOnPhone subtitle={facts} subtitleVariant="mono">
      <View style={styles.heroBand(header?.background ?? theme.colors.surface)} testID="Detail header">
        <Cover title={title} uri={coverUri} width={160} sharedTag={sharedCoverTag(reduceMotion, id)} />
        <FormatBadge label={`${kindLabels[work.kind]} · ${formatLabels[release.format]}`} />
        {item.editionVerified ? null : (
          <>
            <FormatBadge label="Edition unverified" />
            {canEdit && Platform.OS !== 'web' ? (
              <Button
                label="Scan barcode to verify edition"
                onPress={() => {
                  boundScanStore.getState().bind({ libraryId, itemId: id, title, kind: 'itemBarcode' });
                  router.push('/scanner');
                }}
              />
            ) : null}
          </>
        )}
        {work.creators.length ? (
          <Text variant="caption" tone="muted" style={header ? { color: header.text } : undefined}>
            {work.creators.join(', ')}
          </Text>
        ) : null}
      </View>

      <Card>
        <Fact label="Status" value={statusLabels[item.status]} />
        {item.status === 'lent' && item.lentTo ? <Fact label="Lent to" value={item.lentTo} /> : null}
        <Fact label="Condition" value={item.condition ? conditionLabels[item.condition] : 'Not set'} />
        <Fact label="Completeness" value={item.completeness ? completenessLabels[item.completeness] : 'Not set'} />
        <Fact label="Location" value={breadcrumb(locations, item.locationId)} />
        <Fact label="Acquired" value={item.acquiredOn ?? 'Not set'} />
      </Card>

      <Card>
        <Text variant="heading">Notes</Text>
        <Text>{item.notes ?? 'None'}</Text>
      </Card>

      {otherCopies.length > 0 ? <Text tone="muted">{`${copiesInLibrary} ${copiesInLibrary === 1 ? 'copy' : 'copies'} of this work`}</Text> : null}

      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}

      {/* Viewers look; only owners and editors change things (the server refuses the rest with a 403). */}
      {canEdit && (moves.primary || item.status === 'owned') ? (
        <View style={styles.actions}>
          {moves.primary ? (
            <Button label={STATUS_MOVES[moves.primary].label} haptic="commit" busy={update.isPending} onPress={() => runMove(moves.primary!)} />
          ) : null}
          {item.status === 'owned' ? (
            <Button label="Lend…" variant="secondary" onPress={() => router.push({ pathname: '/item/[id]/edit', params: { id, status: 'lent' } })} />
          ) : null}
          {moves.more.map((name) => (
            <Button key={name} label={STATUS_MOVES[name].label} variant="secondary" disabled={update.isPending} onPress={() => runMove(name)} />
          ))}
        </View>
      ) : null}

      {canEdit ? (
        <View style={styles.actions}>
          <Button label="Edit" variant={moves.primary ? 'secondary' : 'primary'} onPress={() => router.push(`/item/${id}/edit`)} />
          <Button label="Move" variant="secondary" onPress={() => setMoving(!moving)} />
          <Button label="Delete" variant="danger" haptic="commit" onPress={del} busy={remove.isPending} />
        </View>
      ) : null}

      {canEdit && moving ? (
        <Sheet title="Move to" onClose={() => setMoving(false)}>
          {flattenTree(locations).map(({ location, depth }) => (
            <ListRow
              key={location.id}
              title={location.name}
              depth={depth}
              subtitle={location.id === item.locationId ? 'Here now' : undefined}
              onPress={() => move(location.id)}
            />
          ))}
        </Sheet>
      ) : null}

      <OtherCopies copies={otherCopies} locations={locations} onOpen={(copyId) => router.push(`/item/${copyId}`)} />
      {done ? <Snackbar message={done.message} actionLabel={done.etag ? 'Undo' : undefined} onAction={undoMove} /> : null}
      <UndoSnackbar />
    </Screen>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.fact}>
      <Text tone="muted" style={styles.factLabel}>
        {label}
      </Text>
      <Text style={styles.factValue}>{value}</Text>
    </View>
  );
}

function OtherCopies({
  copies,
  locations,
  onOpen,
}: {
  copies: ItemDetailDto['otherCopies'];
  locations: Parameters<typeof breadcrumb>[0];
  onOpen: (itemId: string) => void;
}) {
  if (copies.length === 0) return null;
  return (
    <View style={styles.copies}>
      <Text variant="heading">Also in your library</Text>
      {copies.map((copy) => (
        <ListRow
          key={copy.itemId}
          title={[formatLabels[copy.format], copy.platform].filter(Boolean).join(' · ')}
          subtitle={`${statusLabels[copy.status]} · ${breadcrumb(locations, copy.locationId)}`}
          onPress={() => onOpen(copy.itemId)}
        />
      ))}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  // The derived band color arrives per item, so it is a dynamic value; the shape stays in the sheet.
  heroBand: (backgroundColor: string) => ({
    alignItems: 'center',
    gap: theme.spacing.sm,
    padding: theme.spacing.lg,
    borderRadius: theme.radius.lg,
    backgroundColor,
  }),
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  fact: { flexDirection: 'row', gap: theme.spacing.md },
  factLabel: { width: 110 },
  factValue: { flex: 1 },
  copies: { gap: theme.spacing.sm },
}));
