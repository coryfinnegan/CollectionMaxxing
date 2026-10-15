import { onlineManager, useMutation } from '@tanstack/react-query';
import { act, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { Text } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { server } from '@/mocks/server';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { useOwnedLookup, useOwnedSync } from './hooks';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from './ownedCache';
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
});

describe('useOwnedSync', () => {
  function Syncer() {
    useOwnedSync();
    const add = useMutation({ mutationKey: ['createItem'], mutationFn: async () => 'ok' });
    return <Text onPress={() => add.mutate()}>add</Text>;
  }

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
});
