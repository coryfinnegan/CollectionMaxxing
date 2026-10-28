// The dedicated Search view (#458): as-you-type work rows, kind/platform/year
// filters, recent searches, and each work's editions one tap down.
import { onlineManager } from '@tanstack/react-query';
import { act, fireEvent, screen } from '@testing-library/react-native';
import { delay, HttpResponse, http } from 'msw';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { seed } from '@/mocks/catalog';
import { server } from '@/mocks/server';
import type { OwnedCopy } from '@/features/offline/ownedIndex';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { renderWithProviders } from '@/test/render';
import { getRecentStorage, saveRecentSearch, setRecentStorage } from './recent';
import { SearchView, type SearchAnswer, type SearchViewProps } from './SearchView';

const ALIEN_DVD = 'f1000000-0000-4000-8000-000000000001';
const ALIEN_VHS = 'f1000000-0000-4000-8000-000000000002';

beforeEach(() => {
  onlineManager.setOnline(true);
  setRecentStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  onlineManager.setOnline(true);
  setHapticDriver(null);
});

afterAll(() => setRecentStorage(undefined));

const renderSearch = async (props: Partial<SearchViewProps> = {}) => {
  const onPick = jest.fn();
  const onManual = jest.fn();
  await renderWithProviders(<SearchView onPick={onPick} onManual={onManual} {...props} />);
  return { onPick, onManual };
};

const typeQuery = async (text: string) => {
  await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), text);
};

const listenSearches = () => {
  const asked: string[] = [];
  const heard = ({ request }: { request: Request }) => {
    if (request.url.includes('/api/v1/catalog/search')) asked.push(request.url);
  };
  server.events.on('request:start', heard);
  return { asked, stop: () => server.events.removeListener('request:start', heard) };
};

const fakeHaptics = () => {
  const calls: string[] = [];
  const driver: HapticDriver = {
    tap: () => void calls.push('tap'),
    mediumTap: () => void calls.push('medium'),
    longTap: () => void calls.push('long'),
    tick: () => void calls.push('tick'),
    success: () => void calls.push('success'),
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  return calls;
};

const param = (url: string, name: string) => new URL(url).searchParams.get(name);

describe('initial state', () => {
  it('hints at the two-character minimum and asks nothing of the catalog', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      expect(await screen.findByText('Type at least two characters to search.')).toBeTruthy();
      expect(asked).toHaveLength(0);
    } finally {
      stop();
    }
  });

  it('sends no query under two characters', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await typeQuery('a');
      await act(async () => {
        await new Promise((r) => setTimeout(r, 400));
      });
      expect(asked).toHaveLength(0);
      expect(screen.getByText('Type at least two characters to search.')).toBeTruthy();
    } finally {
      stop();
    }
  });

  it('runs a recent search again when tapped', async () => {
    saveRecentSearch(getRecentStorage(), 'alien');
    await renderSearch();
    await fireEvent.press(await screen.findByLabelText('Search again for alien'));
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('clears recent searches', async () => {
    saveRecentSearch(getRecentStorage(), 'alien');
    saveRecentSearch(getRecentStorage(), 'dune');
    await renderSearch();
    await fireEvent.press(await screen.findByLabelText('Clear recent searches'));
    expect(screen.queryByLabelText('Search again for alien')).toBeNull();
    expect(screen.queryByLabelText('Search again for dune')).toBeNull();
    expect(screen.getByText('Type at least two characters to search.')).toBeTruthy();
  });
});

describe('results', () => {
  it('lists one row per work as you type', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await typeQuery('alien');
      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Arrival, Denis Villeneuve · 2016 · 1 edition' })).toBeNull();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) {
        expect(param(url, 'q')).toBe('alien');
        expect(param(url, 'kind')).toBeNull();
        expect(param(url, 'platform')).toBeNull();
        expect(param(url, 'year')).toBeNull();
        expect(param(url, 'scope')).toBeNull();
      }
    } finally {
      stop();
    }
  });

  it('shows the loading state while the catalog answers', async () => {
    server.use(
      http.get('*/api/v1/catalog/search', async () => {
        await delay(500);
        return HttpResponse.json({ items: seed().slice(0, 1), nextCursor: null });
      }),
    );
    await renderSearch();
    await typeQuery('alien');
    expect(await screen.findByLabelText('Searching the catalog')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('searches the pinned query without typing', async () => {
    await renderSearch({ initialQuery: 'alien' });
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('remembers a search that found works', async () => {
    await renderSearch();
    await typeQuery('alien');
    await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' });
    await typeQuery('');
    expect(await screen.findByLabelText('Search again for alien')).toBeTruthy();
  });
});

describe('filters', () => {
  it('narrows by kind through the chips', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.press(await screen.findByLabelText('Kind Books'));
      await typeQuery('ne');
      expect(await screen.findByRole('button', { name: 'Neuromancer, William Gibson · 1984 · 1 edition' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'kind')).toBe('book');
    } finally {
      stop();
    }
  });

  it('clears the kind filter through All', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.press(await screen.findByLabelText('Kind Books'));
      await fireEvent.press(await screen.findByLabelText('All kinds'));
      await typeQuery('ne');
      expect(await screen.findByRole('button', { name: 'Neuromancer, William Gibson · 1984 · 1 edition' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'kind')).toBeNull();
    } finally {
      stop();
    }
  });

  it('shows platform chips for games and narrows by platform', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.press(await screen.findByLabelText('Kind Games'));
      expect(await screen.findByLabelText('All platforms')).toBeTruthy();
      await fireEvent.press(await screen.findByLabelText('Platform N64'));
      await typeQuery('mario');
      expect(await screen.findByRole('button', { name: 'Super Mario 64, Nintendo · 1996 · 1 edition' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) {
        expect(param(url, 'kind')).toBe('game');
        expect(param(url, 'platform')).toBe('Nintendo 64');
      }
      await fireEvent.press(screen.getByLabelText('Platform GB'));
      expect(await screen.findByText('No matches')).toBeTruthy();
    } finally {
      stop();
    }
  });

  it('leaving games clears the platform filter', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.press(await screen.findByLabelText('Kind Games'));
      await fireEvent.press(await screen.findByLabelText('Platform N64'));
      await fireEvent.press(screen.getByLabelText('Kind Movies'));
      expect(screen.queryByLabelText('Platform N64')).toBeNull();
      await typeQuery('ar');
      expect(await screen.findByRole('button', { name: 'Arrival, Denis Villeneuve · 2016 · 1 edition' })).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Super Mario 64, Nintendo · 1996 · 1 edition' })).toBeNull();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'platform')).toBeNull();
    } finally {
      stop();
    }
  });

  it('narrows by year', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.changeText(await screen.findByLabelText('Year'), '1979');
      await typeQuery('alien');
      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'year')).toBe('1979');
    } finally {
      stop();
    }
  });

  it('ignores a partial year', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await fireEvent.changeText(await screen.findByLabelText('Year'), '197');
      await typeQuery('alien');
      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'year')).toBeNull();
    } finally {
      stop();
    }
  });

  it('buzzes select on a filter change', async () => {
    const calls = fakeHaptics();
    await renderSearch();
    await fireEvent.press(await screen.findByLabelText('Kind Books'));
    expect(calls).toContain('tick');
  });
});

describe('editions', () => {
  it('opens the editions list and comes back', async () => {
    await renderSearch();
    await typeQuery('alien');
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' }));
    expect(await screen.findByLabelText('Back to results')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Alien (1979), DVD' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Alien (1980), VHS' })).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Back to results'));
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
    expect(screen.queryByLabelText('Back to results')).toBeNull();
  });

  it('picks an edition', async () => {
    const { onPick } = await renderSearch();
    await typeQuery('alien');
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien (1980), VHS' }));
    expect(onPick).toHaveBeenCalledWith(ALIEN_VHS);
  });

  it('marks the editions the response says are owned', async () => {
    const owned = seed().map((entry) =>
      entry.work.title === 'Alien' ? { ...entry, ownedReleaseIds: [ALIEN_DVD] } : entry,
    );
    server.use(http.get('*/api/v1/catalog/search', () => HttpResponse.json({ items: owned, nextCursor: null })));
    await renderSearch();
    await typeQuery('alien');
    expect(await screen.findByText('In your library')).toBeTruthy();
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions, in your library' }));
    expect(await screen.findByText('You own 1')).toBeTruthy();
  });
});

describe('dead ends', () => {
  it('offers adding it yourself when nothing matches', async () => {
    const { onManual } = await renderSearch();
    await typeQuery('zzz no such title');
    expect(await screen.findByText('No matches')).toBeTruthy();
    await fireEvent.press(await screen.findByLabelText('Add it yourself'));
    expect(onManual).toHaveBeenCalledTimes(1);
  });

  it('offers a retry when the catalog fails', async () => {
    server.use(
      http.get(
        '*/api/v1/catalog/search',
        () => HttpResponse.json({ title: 'Catalog unavailable.', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } }),
        { once: true },
      ),
    );
    await renderSearch();
    await typeQuery('alien');
    expect(await screen.findByText("Couldn't search the catalog")).toBeTruthy();
    await fireEvent.press(await screen.findByLabelText('Try again'));
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('says it is offline instead of hanging', async () => {
    onlineManager.setOnline(false);
    const { asked, stop } = listenSearches();
    try {
      await renderSearch();
      await typeQuery('alien');
      expect(await screen.findByText("You're offline")).toBeTruthy();
      expect(asked).toHaveLength(0);
    } finally {
      stop();
    }
  });
});

describe('library scope', () => {
  it('searches the library first when scoped', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch({ scope: 'library' });
      await typeQuery('alien');
      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(asked.length).toBeGreaterThan(0);
      for (const url of asked) expect(param(url, 'scope')).toBe('library');
    } finally {
      stop();
    }
  });
});

describe('offline answers', () => {
  const ownedCopy = (overrides: Partial<OwnedCopy> = {}): OwnedCopy => ({
    itemId: 'item-alien',
    releaseId: ALIEN_DVD,
    workId: 'work-alien',
    title: 'Alien',
    format: 'dvd',
    platform: null,
    locationPath: 'Living room › Shelf B',
    ...overrides,
  });

  const offlineSource = (copies: OwnedCopy[]) => ({
    searchTitles: jest.fn((_query: string) => copies),
    staleLine: 'Based on your last sync, 2 h ago',
  });

  const failSearchOnce = () =>
    server.use(
      http.get(
        '*/api/v1/catalog/search',
        () => HttpResponse.json({ title: 'Catalog unavailable.', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } }),
        { once: true },
      ),
    );

  it('answers from the owned cache when offline', async () => {
    onlineManager.setOnline(false);
    const { asked, stop } = listenSearches();
    try {
      const source = offlineSource([ownedCopy(), ownedCopy({ itemId: 'item-aliens', title: 'Aliens', format: 'bluray' })]);
      await renderSearch({ offline: source });
      await typeQuery('alien');
      expect(await screen.findByText('Based on your last sync, 2 h ago')).toBeTruthy();
      expect(screen.getByText('DVD · Living room › Shelf B')).toBeTruthy();
      expect(screen.getByText('Blu-ray · Living room › Shelf B')).toBeTruthy();
      expect(screen.queryByText("You're offline")).toBeNull();
      expect(source.searchTitles).toHaveBeenCalledWith('alien');
      expect(asked).toHaveLength(0);
    } finally {
      stop();
    }
  });

  it('shows no matches without a manual path when the owned cache is empty', async () => {
    onlineManager.setOnline(false);
    await renderSearch({ offline: offlineSource([]), onManual: undefined });
    await typeQuery('zzz no such title');
    expect(await screen.findByText('No matches')).toBeTruthy();
    expect(screen.getByText('Based on your last sync, 2 h ago')).toBeTruthy();
    expect(screen.queryByLabelText('Add it yourself')).toBeNull();
  });

  it('falls back to the owned cache when the catalog fails', async () => {
    failSearchOnce();
    await renderSearch({ offline: offlineSource([ownedCopy()]) });
    await typeQuery('alien');
    expect(await screen.findByText("Couldn't search the catalog. Check your connection.")).toBeTruthy();
    expect(screen.getByText('DVD · Living room › Shelf B')).toBeTruthy();
  });

  it('offers a retry when the catalog fails and the owned cache is empty', async () => {
    failSearchOnce();
    await renderSearch({ offline: offlineSource([]) });
    await typeQuery('alien');
    expect(await screen.findByText("Couldn't search the catalog")).toBeTruthy();
    await fireEvent.press(await screen.findByLabelText('Try again'));
    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('shows no matches without an action when the host has no manual path', async () => {
    await renderSearch({ onManual: undefined });
    await typeQuery('zzz no such title');
    expect(await screen.findByText('No matches')).toBeTruthy();
    expect(screen.queryByLabelText('Add it yourself')).toBeNull();
  });
});

describe('pinned answers (fixtures)', () => {
  const pinned = (overrides: Partial<SearchAnswer> = {}): SearchAnswer => ({
    items: [],
    isLoading: false,
    isError: false,
    error: null,
    refetch: jest.fn(),
    ...overrides,
  });

  it('renders pinned results without touching the catalog', async () => {
    const { asked, stop } = listenSearches();
    try {
      await renderSearch({ initialQuery: 'alien', search: pinned({ items: seed() }) });
      expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
      expect(asked).toHaveLength(0);
    } finally {
      stop();
    }
  });

  it('renders the pinned loading state', async () => {
    await renderSearch({ initialQuery: 'alien', search: pinned({ isLoading: true }) });
    expect(await screen.findByLabelText('Searching the catalog')).toBeTruthy();
  });

  it('renders the pinned error state', async () => {
    await renderSearch({ initialQuery: 'alien', search: pinned({ isError: true, error: new Error('boom') }) });
    expect(await screen.findByText("Couldn't search the catalog")).toBeTruthy();
  });
});
