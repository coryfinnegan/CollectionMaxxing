// Typed search from the scanner: the Search view scoped to the library (owned answers first), the owned index when offline.
import { onlineManager } from '@tanstack/react-query';
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { localDateString } from '@/lib/dates';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { seed } from '@/mocks/catalog';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { setRecentStorage } from '@/features/search/recent';
import { createOwnedIndex } from '@/features/offline/ownedIndex';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { entry } from '@/features/offline/testData';
import { ScannerSearch } from './ScannerSearch';

const ALIEN_DVD = 'f1000000-0000-4000-8000-000000000001';
const ALIEN_VHS = 'f1000000-0000-4000-8000-000000000002';

const seedOwned = (syncedAt: number | null = Date.now()) => {
  setOwnedStorage(new MemoryOwnedStorage());
  getOwnedCache(MOCK_LIBRARY_ID).commit({
    index: createOwnedIndex([
      entry({ title: 'Alien', format: 'dvd', releaseId: ALIEN_DVD, workId: 'work-alien', locationPath: 'Living room › Shelf B' }),
      entry({ title: 'Aliens', format: 'bluray', locationPath: 'Living room › Shelf B' }),
    ]),
    cursor: null,
    syncedAt,
  });
};

beforeEach(() => {
  onlineManager.setOnline(true);
  setOwnedStorage(new MemoryOwnedStorage());
  setRecentStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  onlineManager.setOnline(true);
});

afterAll(() => {
  setOwnedStorage(undefined);
  setRecentStorage(undefined);
});

const listenSearches = () => {
  const asked: string[] = [];
  const heard = ({ request }: { request: Request }) => {
    if (request.url.includes('/api/v1/catalog/search')) asked.push(request.url);
  };
  server.events.on('request:start', heard);
  return { asked, stop: () => server.events.removeListener('request:start', heard) };
};

const searchFor = async (text: string) => {
  await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), text);
};

describe('scanner search', () => {
  it('searches the catalog scoped to the library, one row per work', async () => {
    seedOwned();
    const { asked, stop } = listenSearches();
    try {
      await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

      await searchFor('alien');

      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(url).toContain('scope=library');
    } finally {
      stop();
    }
  });

  it('marks the editions the library-scoped response says are owned', async () => {
    seedOwned();
    const owned = seed().map((result) =>
      result.work.title === 'Alien' ? { ...result, ownedReleaseIds: [ALIEN_DVD] } : result,
    );
    server.use(http.get('*/api/v1/catalog/search', () => HttpResponse.json({ items: owned, nextCursor: null })));
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

    await searchFor('alien');

    expect(await screen.findByText('In your library')).toBeTruthy();
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions, in your library' }));
    expect(await screen.findByText('You own 1')).toBeTruthy();
  });

  it('adds the picked edition as a search match and closes the sheet', async () => {
    seedOwned();
    const sent: string[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
        sent.push(await request.text());
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    const onClose = jest.fn();
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);

    await searchFor('alien');
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien (1980), VHS' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]!)).toMatchObject({ releaseId: ALIEN_VHS, addedVia: 'search', acquiredOn: localDateString() });
  });

  it('adds a provider-only edition by its provider and id, the second row opening the second work', async () => {
    seedOwned();
    const sent: string[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
        sent.push(await request.text());
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    const onClose = jest.fn();
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);

    await searchFor('doom');
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom II, id Software · 1994 · 1 edition' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom II (1994), Game disc, GT Interactive' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(sent).toHaveLength(1);
    const body = JSON.parse(sent[0]!) as Record<string, unknown>;
    expect(body).toMatchObject({ providerRef: { provider: 'igdb', externalId: 'doom-ii-1994' }, addedVia: 'search', acquiredOn: localDateString() });
    expect(body).not.toHaveProperty('releaseId');
  });

  it('shows the add error without closing when the pick fails', async () => {
    seedOwned();
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', () =>
        HttpResponse.json({ title: 'Already in your library.', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
      ),
    );
    const onClose = jest.fn();
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);

    await searchFor('alien');
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien (1980), VHS' }));

    expect(await screen.findByText('Already in your library.')).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('offers no manual path when nothing matches', async () => {
    seedOwned();
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

    await searchFor('zzz no such title');

    expect(await screen.findByText('No matches')).toBeTruthy();
    expect(screen.queryByLabelText('Add it yourself')).toBeNull();
  });

  it('searches the owned copy when offline and says how old it is', async () => {
    onlineManager.setOnline(false);
    seedOwned(Date.now() - 2 * 3_600_000);
    const { asked, stop } = listenSearches();
    try {
      await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

      await searchFor('alien');

      expect(await screen.findByText('DVD · Living room › Shelf B')).toBeTruthy();
      expect(await screen.findByText('Blu-ray · Living room › Shelf B')).toBeTruthy();
      expect(screen.getByText('Based on your last sync, 2 h ago')).toBeTruthy();
      expect(asked).toHaveLength(0);
    } finally {
      stop();
    }
  });

  it('answers from the owned copy when the catalog fails', async () => {
    seedOwned();
    server.use(
      http.get(
        '*/api/v1/catalog/search',
        () => HttpResponse.json({ title: 'Catalog unavailable.', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } }),
        { once: true },
      ),
    );
    await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

    await searchFor('alien');

    expect(await screen.findByText("Couldn't search the catalog. Check your connection.")).toBeTruthy();
    expect(await screen.findByText('DVD · Living room › Shelf B')).toBeTruthy();
  });
});
