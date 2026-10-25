// Typed search from the scanner: the catalog scoped to the library (owned answers first), the owned index when offline.
import { onlineManager } from '@tanstack/react-query';
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { createOwnedIndex } from '@/features/offline/ownedIndex';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { entry } from '@/features/offline/testData';
import { ScannerSearch } from './ScannerSearch';

const ALIEN_DVD = 'f1000000-0000-4000-8000-000000000001';

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
});

afterEach(() => {
  onlineManager.setOnline(true);
});

afterAll(() => setOwnedStorage(undefined));

describe('scanner search', () => {
  it('searches the catalog scoped to the library and says what you own', async () => {
    seedOwned();
    const asked: string[] = [];
    const heard = ({ request }: { request: Request }) => {
      if (request.url.includes('/api/v1/catalog/search')) asked.push(request.url);
    };
    server.events.on('request:start', heard);
    try {
      await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

      fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'alien');

      expect(await screen.findByText('DVD · 1979 · You own 1 · Living room › Shelf B')).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(url).toContain('scope=library');
    } finally {
      server.events.removeListener('request:start', heard);
    }
  });

  it('searches the owned copy when offline and says how old it is', async () => {
    onlineManager.setOnline(false);
    seedOwned(Date.now() - 2 * 3_600_000);
    const asked: string[] = [];
    const heard = ({ request }: { request: Request }) => {
      if (request.url.includes('/api/v1/catalog/search')) asked.push(request.url);
    };
    server.events.on('request:start', heard);
    try {
      await renderWithProviders(<ScannerSearch libraryId={MOCK_LIBRARY_ID} onClose={() => undefined} />);

      fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'alien');

      expect(await screen.findByText('DVD · Living room › Shelf B')).toBeTruthy();
      expect(await screen.findByText('Blu-ray · Living room › Shelf B')).toBeTruthy();
      expect(screen.getByText('Based on your last sync, 2 h ago')).toBeTruthy();
      expect(asked).toHaveLength(0);
    } finally {
      server.events.removeListener('request:start', heard);
    }
  });
});
