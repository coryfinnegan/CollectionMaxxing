// Me > Recently deleted: items stay in the bin for 30 days. Editors and owners can restore them.
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { DeletedItemDto } from '@/api/generated/models';
import { getListDeletedItemsQueryKey, useListDeletedItems } from '@/api/generated/trash/trash';
import { useRestoreItem } from '@/api/generated/items/items';
import { useInvalidateLibrary } from '@/features/item/useItem';
import { friendlyError } from '@/lib/problem';
import { useNow } from '@/lib/useNow';
import { Button, EmptyState, ListRow, Card, LoadingState, Screen, Text } from '@/ui';
import { BackButton } from './BackButton';
import { useMyRole } from './useMyRole';

const DAY = 86_400_000;

export function daysLeft(purgesAt: string, now = Date.now()): string {
  const days = Math.ceil((new Date(purgesAt).getTime() - now) / DAY);
  if (days <= 0) return 'Removed for good today';
  return days === 1 ? '1 day left' : `${days} days left`;
}

export function RecentlyDeletedScreen() {
  const { libraryId, role, isLoading } = useMyRole();
  if (isLoading || !libraryId) {
    return (
      <Screen title="Recently deleted">
        <BackButton />
        <LoadingState label="Loading…" />
      </Screen>
    );
  }
  if (role === 'viewer' || !role) {
    return (
      <Screen title="Recently deleted">
        <BackButton />
        <EmptyState title="Only editors and the owner can see and restore deleted items." />
      </Screen>
    );
  }
  return <Bin libraryId={libraryId} />;
}

function Bin({ libraryId }: { libraryId: string }) {
  const queryClient = useQueryClient();
  const invalidateLibrary = useInvalidateLibrary();
  const list = useListDeletedItems(libraryId);
  const restore = useRestoreItem();
  const [message, setMessage] = useState<string | null>(null);
  const now = useNow();
  const items: DeletedItemDto[] = list.data?.status === 200 ? list.data.data.items : [];

  const restoreOne = (item: DeletedItemDto) => {
    setMessage(null);
    restore.mutate(
      { libraryId, id: item.id },
      {
        onSuccess: () => {
          setMessage(`${item.title} is back in your library.`);
          queryClient.invalidateQueries({ queryKey: getListDeletedItemsQueryKey(libraryId) });
          // Same full invalidation as the Undo path, so the item reappears in the library views.
          invalidateLibrary(libraryId);
        },
        onError: (e) => setMessage(friendlyError(e)),
      },
    );
  };

  return (
    <Screen title="Recently deleted" subtitle="Deleted items stay here for 30 days.">
      <BackButton />
      {message ? <Text accessibilityRole="alert">{message}</Text> : null}
      {list.isLoading ? <LoadingState label="Loading…" /> : null}
      {list.isError ? <Text tone="danger" accessibilityRole="alert">{friendlyError(list.error)}</Text> : null}
      {!list.isLoading && !list.isError && items.length === 0 ? (
        <EmptyState title="Nothing deleted" body="Items you delete wait here for 30 days, so you can bring them back." />
      ) : null}
      {items.length > 0 ? (
        <Card>
          {items.map((item) => (
            <ListRow
              key={item.id}
              title={item.title}
              subtitle={daysLeft(item.purgesAt, now)}
              trailing={<Button label="Restore" accessibilityLabel={`Restore ${item.title}`} variant="secondary" onPress={() => restoreOne(item)} />}
            />
          ))}
        </Card>
      ) : null}
    </Screen>
  );
}
