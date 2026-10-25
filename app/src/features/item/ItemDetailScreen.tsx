// Item detail: cover, facts, location breadcrumb, other copies, and Edit / Move / Delete (with Undo).
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';
import type { ItemDetailDto } from '@/api/generated/models';
import { completenessLabels, conditionLabels, formatLabels, kindLabels, statusLabels } from '@/features/library/labels';
import { flattenTree } from '@/features/locations/tree';
import { useActiveLibraryId, useLocationsList } from '@/features/locations/useLocations';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Card, Cover, EmptyState, FormatBadge, ListRow, LoadingState, Screen, Sheet, Text } from '@/ui';
import { sharedCoverTag } from '@/ui/motion';
import { breadcrumb } from './breadcrumb';
import { UndoSnackbar } from './UndoSnackbar';
import { useItemDetail, useItemMutations, useUndoStore } from './useItem';

export function ItemDetailScreen({ id }: { id: string }) {
  const { libraryId, isLoading } = useActiveLibraryId();
  if (isLoading || !libraryId) {
    return (
      <Screen>
        <LoadingState label="Loading this item…" />
      </Screen>
    );
  }
  return <Detail libraryId={libraryId} id={id} />;
}

function Detail({ libraryId, id }: { libraryId: string; id: string }) {
  const router = useRouter();
  const reduceMotion = useReduceMotion();
  const { detail, etag, isLoading, isError, error, refetch } = useItemDetail(libraryId, id);
  const { locations } = useLocationsList(libraryId);
  const { update, remove } = useItemMutations(libraryId);
  const setDeleted = useUndoStore((s) => s.setDeleted);
  const [moving, setMoving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

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
        <EmptyState
          title="Couldn't load this item"
          body={friendlyError(error)}
          actionLabel="Try again"
          onAction={() => refetch()}
        />
      </Screen>
    );
  }

  const { item, work, release } = detail;
  const title = item.customTitle ?? work.title;
  const facts = [formatLabels[release.format], release.platform?.name, release.year ?? work.originalYear, release.publisher]
    .filter(Boolean)
    .join(' · ');

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

  const del = () => {
    setMessage(null);
    remove.mutate(
      { id, etag },
      {
        onSuccess: () => {
          setDeleted({ libraryId, id, title });
          router.back();
        },
        onError: (e) => setMessage(friendlyError(e)),
      },
    );
  };

  return (
    <Screen title={title} showTitleOnPhone subtitle={facts} subtitleVariant="mono">
      <View style={{ alignItems: 'center', gap: spacing.sm }}>
        <Cover title={title} uri={release.coverUrl ?? work.coverUrl} width={160} sharedTag={sharedCoverTag(reduceMotion, id)} />
        <FormatBadge label={`${kindLabels[work.kind]} · ${formatLabels[release.format]}`} />
        {work.creators.length ? <Text variant="caption" tone="muted">{work.creators.join(', ')}</Text> : null}
      </View>

      <Card>
        <Fact label="Status" value={statusLabels[item.status]} />
        {item.status === 'lent' && item.lentTo ? <Fact label="Lent to" value={item.lentTo} /> : null}
        <Fact label="Condition" value={item.condition ? conditionLabels[item.condition] : 'Not set'} />
        <Fact label="Completeness" value={item.completeness ? completenessLabels[item.completeness] : 'Not set'} />
        <Fact label="Location" value={breadcrumb(locations, item.locationId)} />
        <Fact label="Acquired" value={item.acquiredOn ?? 'Not set'} />
        <Fact label="Notes" value={item.notes ?? 'None'} />
      </Card>

      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}

      <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
        <Button label="Edit" onPress={() => router.push(`/item/${id}/edit`)} />
        <Button label="Move" variant="secondary" onPress={() => setMoving(!moving)} />
        <Button label="Delete" variant="danger" haptic="commit" onPress={del} busy={remove.isPending} />
      </View>

      {moving ? (
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

      <OtherCopies copies={detail.otherCopies} locations={locations} onOpen={(copyId) => router.push(`/item/${copyId}`)} />
      <UndoSnackbar />
    </Screen>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <View style={{ flexDirection: 'row', gap: spacing.md }}>
      <Text tone="muted" style={{ width: 110 }}>
        {label}
      </Text>
      <Text style={{ flex: 1 }}>{value}</Text>
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
    <View style={{ gap: spacing.sm }}>
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
