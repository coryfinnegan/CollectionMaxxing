import { onlineManager, useMutation } from '@tanstack/react-query';
import { act, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { Text } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { server } from '@/mocks/server';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { getBulkItemsMutationKey, getRematchItemMutationKey } from '@/api/generated/items/items';
import { useOwnedLookup, useOwnedSync } from './hooks';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from './ownedCache';
import { createOwnedIndex } from './ownedIndex';
import { syncLibrary } from './sync';

const H = 3_600_000;

function Answer({ code, now }: { code: string; now?: () => number }) {
  const a = useOwnedLookup(code, now);
  return (
    <>
      <Text>{`source:${a.source}`}</Text>
      <Text>{`owned:${a.owned.map((c) => `${c.title} (${c.format}) at ${c.locationPath}`).join(' | ') || 'none'}`}</Text>
      <Text>{`soft:${a.softMatches.map((c) => c.format).join(',') || 'none'}`}</Text>
      <Text>{a.notInLibrary ? a.notInLibraryText : 'has-answer'}</Text>
      {a.staleMessage ? <Text>{a.staleMessage}</Text> : null}
    </>
  );
}

beforeEach(async () => {
  resetOwnershipMock();
  setOwnedStorage(new MemoryOwnedStorage());
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  onlineManager.setOnline(true);
  await syncLibrary(MOCK_LIBRARY_ID, () => 0);
});
afterEach(() => {
  onlineManager.setOnline(true);
  useLibraryStore.setState({ activeLibraryId: null });
});
afterAll(() => setOwnedStorage(undefined));

describe('useOwnedLookup offline', () => {
  beforeEach(() => onlineManager.setOnline(false));

  it('answers from the local copy with the location and how old the copy is', async () => {
    await renderWithProviders(<Answer code="042100005264" now={() => 2 * H + 5 * 60_000} />);

    expect(await screen.findByText('owned:Jaws (vhs) at Living room › Shelf B')).toBeTruthy();
    expect(screen.getByText('source:local')).toBeTruthy();
    expect(screen.getByText('Based on your last sync, 2 h ago')).toBeTruthy();
  });

  it('offers another format of the same work as a soft match', async () => {
    await renderWithProviders(<Answer code="042100005264" />);

    expect(await screen.findByText('soft:dvd')).toBeTruthy();
  });

  it('names the oldest copy first when several exact copies sit at different shelves', async () => {
    const state = getOwnedCache(MOCK_LIBRARY_ID).state;
    const oldest = state.index.entries().find((e) => e.identifiers.includes('0042100005264'))!;
    const newer = { ...oldest, itemId: '00000009-0000-4000-8000-000000000000', locationPath: 'Basement' };
    getOwnedCache(MOCK_LIBRARY_ID).commit({ ...state, index: createOwnedIndex([newer, ...state.index.entries()]) });

    await renderWithProviders(<Answer code="042100005264" />);

    expect(await screen.findByText('owned:Jaws (vhs) at Living room › Shelf B | Jaws (vhs) at Basement')).toBeTruthy();
    expect(screen.getByText('source:local')).toBeTruthy();
  });

  it('says "Not in your library" for a code the library does not own', async () => {
    await renderWithProviders(<Answer code="4006381333931" />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
  });

  it('works for the other forms of a code', async () => {
    await renderWithProviders(<Answer code="0306406152" />);

    expect(await screen.findByText('owned:Dune (paperback) at Office › Shelf 1')).toBeTruthy();
  });

  it('asks the person to connect once when nothing has ever synced', async () => {
    getOwnedCache(MOCK_LIBRARY_ID).clear();

    await renderWithProviders(<Answer code="042100005264" />);

    expect(await screen.findByText(/hasn't synced yet/)).toBeTruthy();
  });

  it('trusts the remembered library while the list cannot load, even a stale id', async () => {
    // Offline the list stays paused and cannot judge the stored id, so the local copy answers from it.
    const synced = getOwnedCache(MOCK_LIBRARY_ID).state;
    getOwnedCache('stale-library-id').commit({ ...synced });
    getOwnedCache(MOCK_LIBRARY_ID).clear(); // only the stale library's copy has the answer now
    useLibraryStore.setState({ activeLibraryId: 'stale-library-id' });

    await renderWithProviders(<Answer code="042100005264" now={() => 2 * H + 5 * 60_000} />);

    expect(await screen.findByText('owned:Jaws (vhs) at Living room › Shelf B')).toBeTruthy();
  });
});

describe('useOwnedLookup offline, before the first sync since the update', () => {
  // The last release's copy: eight columns, no status. It is Jaws on VHS, in the library.
  const v1 = JSON.stringify({
    v: 1,
    cursor: '3',
    syncedAt: 0,
    rows: [['00000001-0000-4000-8000-000000000000', '00000101-0000-4000-8000-000000000000', '00000201-0000-4000-8000-000000000000', 'Jaws', 'vhs', null, ['0042100005264'], 'Living room › Shelf B']],
  });

  it('still says what you own, from the old copy', async () => {
    const storage = new MemoryOwnedStorage();
    storage.set(`owned-index-v1:${MOCK_LIBRARY_ID}`, v1);
    setOwnedStorage(storage); // the app was updated and opened in a store with no signal
    onlineManager.setOnline(false);

    await renderWithProviders(<Answer code="042100005264" now={() => 2 * H} />);

    expect(await screen.findByText('owned:Jaws (vhs) at Living room › Shelf B')).toBeTruthy();
    expect(screen.queryByText('Not in your library')).toBeNull();
    expect(screen.getByText('source:local')).toBeTruthy();
  });
});

describe('useOwnedLookup: wishlist and sold', () => {
  const ALIEN = { releaseId: '00000160-0000-4000-8000-000000000000', workId: '00000260-0000-4000-8000-000000000000' };
  const row = (n: number, status: 'wishlist' | 'sold' | 'missing') => ({
    itemId: `0000006${n}-0000-4000-8000-000000000000`,
    deleted: false,
    ...ALIEN,
    title: 'Alien',
    format: 'dvd' as const,
    platform: null,
    identifiers: ['5051889000000'],
    locationPath: null,
    status,
  });

  function Facts({ releaseId }: { releaseId: string }) {
    const a = useOwnedLookup({ releaseId });
    return (
      <>
        <Text>{`source:${a.source}`}</Text>
        <Text>{`owned:${a.owned.map((c) => `${c.title} ${c.status}`).join(',') || 'none'}`}</Text>
        <Text>{`wishlist:${a.wishlistItemId ?? 'none'}`}</Text>
        <Text>{`sold:${a.soldItemId ?? 'none'}`}</Text>
      </>
    );
  }

  it('says offline that a release is on the wishlist, and that it is not owned', async () => {
    upsertOwned(row(1, 'wishlist'));
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
    onlineManager.setOnline(false);

    await renderWithProviders(<Facts releaseId={ALIEN.releaseId} />);

    expect(await screen.findByText('wishlist:00000061-0000-4000-8000-000000000000')).toBeTruthy();
    expect(screen.getByText('owned:none')).toBeTruthy();
    expect(screen.getByText('source:local')).toBeTruthy();
  });

  it('says offline that a release was sold, and that it is not owned', async () => {
    upsertOwned(row(2, 'sold'));
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
    onlineManager.setOnline(false);

    await renderWithProviders(<Facts releaseId={ALIEN.releaseId} />);
    expect(await screen.findByText('sold:00000062-0000-4000-8000-000000000000')).toBeTruthy();
    expect(screen.getByText('owned:none')).toBeTruthy();
  });

  it('takes the facts from the server when it answers', async () => {
    upsertOwned(row(1, 'wishlist'));
    await renderWithProviders(<Facts releaseId={ALIEN.releaseId} />);

    expect(await screen.findByText('source:server')).toBeTruthy();
    expect(screen.getByText('wishlist:00000061-0000-4000-8000-000000000000')).toBeTruthy();
    expect(screen.getByText('owned:none')).toBeTruthy();
  });
});

describe('useOwnedLookup online', () => {
  it('lets the server answer win and shows no stale notice', async () => {
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership', () =>
        HttpResponse.json({
          owned: true,
          count: 1,
          sameWorkOtherFormat: [],
          copies: [
            { itemId: 'i', releaseId: 'r', workId: 'w', title: 'Fresh from server', format: 'vhs', platform: null, status: 'owned', locationId: null, locationPath: 'Garage' },
          ],
        }),
      ),
    );

    await renderWithProviders(<Answer code="042100005264" />);

    expect(await screen.findByText('owned:Fresh from server (vhs) at Garage')).toBeTruthy();
    expect(screen.getByText('source:server')).toBeTruthy();
    expect(screen.queryByText(/Based on your last sync/)).toBeNull();
  });

  it('falls back to the local copy, with its age, when the server cannot be reached', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/ownership', () => HttpResponse.error()));

    await renderWithProviders(<Answer code="042100005264" now={() => H} />);

    expect(await screen.findByText('Based on your last sync, 1 h ago')).toBeTruthy();
    expect(screen.getByText('owned:Jaws (vhs) at Living room › Shelf B')).toBeTruthy();
  });

  it('asks the server about the first library on a fresh sign-in', async () => {
    useLibraryStore.setState({ activeLibraryId: null });
    const asked: unknown[] = [];
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership', ({ params }) => {
        asked.push(params.libraryId);
        return HttpResponse.json({ owned: false, count: 0, sameWorkOtherFormat: [], copies: [] });
      }),
    );

    await renderWithProviders(<Answer code="042100005264" />);

    await waitFor(() => expect(asked).toEqual([MOCK_LIBRARY_ID]));
  });
});

describe('useOwnedSync', () => {
  function Syncer({ mutationKey = ['createItem'] }: { mutationKey?: readonly unknown[] }) {
    useOwnedSync();
    const add = useMutation({ mutationKey, mutationFn: async () => 'ok' });
    return <Text onPress={() => add.mutate()}>add</Text>;
  }

  it.each([
    ['bulk item changes', getBulkItemsMutationKey()],
    ['rematching an item', getRematchItemMutationKey()],
  ])('re-syncs after %s', async (_label, mutationKey) => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ request }) => {
        calls += 1;
        return HttpResponse.json({ entries: [], nextCursor: new URL(request.url).searchParams.get('since') ?? '0', hasMore: false });
      }),
    );

    await renderWithProviders(<Syncer mutationKey={mutationKey} />);
    await waitFor(() => expect(calls).toBe(1));

    await act(async () => {
      screen.getByText('add').props.onPress();
    });

    await waitFor(() => expect(calls).toBe(2));
  });

  it('syncs on start and again after the user adds an item', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ request }) => {
        calls += 1;
        return HttpResponse.json({ entries: [], nextCursor: new URL(request.url).searchParams.get('since') ?? '0', hasMore: false });
      }),
    );

    await renderWithProviders(<Syncer />);
    await waitFor(() => expect(calls).toBe(1));

    upsertOwned({
      itemId: '0000000a-0000-4000-8000-000000000000', deleted: false, releaseId: null, workId: null, title: 'x', format: null, platform: null, identifiers: [], locationPath: null,
    });
    await act(async () => {
      screen.getByText('add').props.onPress();
    });

    await waitFor(() => expect(calls).toBe(2));
  });

  it('syncs the library on screen once the list arrives, not a stale stored id', async () => {
    useLibraryStore.setState({ activeLibraryId: 'stale-library-id' });
    const synced: unknown[] = [];
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ params, request }) => {
        synced.push(params.libraryId);
        return HttpResponse.json({ entries: [], nextCursor: new URL(request.url).searchParams.get('since') ?? '0', hasMore: false });
      }),
    );

    await renderWithProviders(<Syncer />);

    // The stored id may be tried while the list loads; the sync converges on the resolved library.
    await waitFor(() => expect(synced).toContain(MOCK_LIBRARY_ID));
    expect(synced[synced.length - 1]).toBe(MOCK_LIBRARY_ID);
  });
});
