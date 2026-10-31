// Typed search from the scanner: the dedicated Search view scoped to this
// library (owned answers first), the owned copy when offline. Picking an
// edition adds it as a search match and leaves the scanner to its flow.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRef, useState } from 'react';
import type { CreateItemRequest } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { useItemMutations } from '@/features/item/useItem';
import { staleCopyMessage } from '@/features/offline/copy';
import { getOwnedCache } from '@/features/offline/ownedCache';
import type { PickedRelease } from '@/features/search/pick';
import { SearchView } from '@/features/search/SearchView';
import { localDateString } from '@/lib/dates';
import { friendlyError } from '@/lib/problem';
import { Sheet, Text } from '@/ui';

export function ScannerSearch({ libraryId, onClose }: { libraryId: string; onClose(): void }) {
  const { create } = useItemMutations(libraryId);
  const key = useRef(newIdempotencyKey());
  // A key replays the first payload it was sent with, so it belongs to one payload:
  // a changed payload needs a fresh key, while retrying the same payload keeps its key.
  const lastPayload = useRef<string | null>(null);
  const [now] = useState(() => Date.now()); // the stale line is computed once per opening, not per keystroke
  const [error, setError] = useState<string | null>(null);
  const cache = getOwnedCache(libraryId);

  const pick = (picked: PickedRelease) => {
    // Acquired is the day it was added (#441): the device stamps its local day, so an
    // evening add stays on today. The server defaults the same day when it is missing.
    const dated: CreateItemRequest = { ...picked, addedVia: 'search', acquiredOn: localDateString() };
    const payload = JSON.stringify(dated);
    if (payload !== lastPayload.current) {
      key.current = newIdempotencyKey();
      lastPayload.current = payload;
    }
    setError(null);
    create.mutate(
      { data: dated, idempotencyKey: key.current },
      {
        onSuccess: () => {
          key.current = newIdempotencyKey();
          lastPayload.current = null;
          onClose();
        },
        onError: (e) => setError(friendlyError(e)),
      },
    );
  };

  return (
    <Sheet title="Search" onClose={onClose}>
      <SearchView
        scope="library"
        onPick={pick}
        offline={{
          searchTitles: (query) => cache.state.index.searchTitles(query),
          staleLine: staleCopyMessage(cache.state.syncedAt, now),
        }}
      />
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
    </Sheet>
  );
}
