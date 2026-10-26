import { fireEvent, screen, waitFor, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { setCastleStorage, useCastleThemeSetting } from '@/ui/castle/mode';
import { setMascotStorage, useDragonAppearance } from '@/ui/mascot/appearances';
import { searchPlaceholder } from './LibraryGrid';
import { LibraryView } from './LibraryView';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

let requests: URL[] = [];
const listRequests = () => requests.filter((u) => u.pathname.endsWith('/items'));

beforeEach(() => {
  resetItems();
  requests = [];
  mockRouter.push.mockClear();
  server.events.on('request:start', ({ request }) => requests.push(new URL(request.url)));
});
afterEach(() => server.events.removeAllListeners());

describe('the Library header', () => {
  // ADR-024: TestFlight tells testers about new builds and installs them, so the app no longer asks the server.
  it('does not ask the server about app updates', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(requests.filter((u) => u.pathname.startsWith('/api/v1/app/'))).toEqual([]);
    expect(screen.queryByText(/is ready/)).toBeNull();
  });
});

describe('LibraryView grid', () => {
  it('shows items from the library with their format', async () => {
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(screen.getByLabelText('The Matrix, DVD')).toBeTruthy();
    expect(screen.getByLabelText('Super Mario 64, Cartridge, Nintendo 64')).toBeTruthy();
  });

  it('opens an item when its cover is pressed', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await fireEvent.press(await screen.findByLabelText('The Matrix, DVD'));
    expect(mockRouter.push).toHaveBeenCalledWith('/item/c0000000-0000-4000-8000-000000000001');
  });

  it('shows the empty-state copy when the library has no items', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items: [], nextCursor: null })));
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
    expect(screen.getByText('Point your camera at a shelf or a stack of cases.')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Scan your first shelf'));
    expect(mockRouter.push).toHaveBeenCalledWith('/scan');
  });

  it('shows friendly copy when the list fails to load', async () => {
    server.use(
      http.get('*/api/v1/libraries/:libraryId/items', () =>
        HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404, headers: { 'Content-Type': 'application/problem+json' } }),
      ),
    );
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText("Couldn't load your library")).toBeTruthy();
    expect(screen.getByText("That's no longer here. Refresh and try again.")).toBeTruthy();
  });

  it('shows the shared loading state while the list loads', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('*/api/v1/libraries/:libraryId/items', async () => {
        await gate;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    await renderWithProviders(<LibraryView title="Library" />);

    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Loading your library…')).toBeTruthy();

    release();
    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
  });

  it('says nothing matches, rather than "nothing here", when a filter finds nothing', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.changeText(screen.getByLabelText('Search'), 'zzzz');

    expect(await screen.findByText('Nothing matches.')).toBeTruthy();
  });

  it('asks only for wishlist items in the Wishlist view', async () => {
    await renderWithProviders(<LibraryView title="Wishlist" fixedStatus="wishlist" />);

    expect(await screen.findByText('Blade Runner')).toBeTruthy();
    expect(screen.queryByText('The Matrix')).toBeNull();
    expect(listRequests().every((u) => u.searchParams.get('status') === 'wishlist')).toBe(true);
  });

  it('asks only for lent items in the Lent out view, with its own empty copy', async () => {
    await renderWithProviders(<LibraryView title="Lent out" fixedStatus="lent" />);

    expect(await screen.findByText('GoldenEye 007')).toBeTruthy();
    expect(listRequests().every((u) => u.searchParams.get('status') === 'lent')).toBe(true);
  });

  it('switches to Needs attention from its chip, and back', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Needs attention'));
    expect(await screen.findByText('Nothing needs attention.')).toBeTruthy();
    expect(screen.queryByText('The Matrix')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Needs attention'));
    expect(await screen.findByText('The Matrix')).toBeTruthy();
  });

  it('shows no Needs attention chip in the Wishlist view', async () => {
    await renderWithProviders(<LibraryView title="Wishlist" fixedStatus="wishlist" />);

    expect(await screen.findByText('Blade Runner')).toBeTruthy();
    expect(screen.queryByLabelText('Needs attention')).toBeNull();
  });

  it('loads the next page on scroll, and starts from the top when a filter changes', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');
    expect(listRequests()).toHaveLength(1);
    expect(listRequests()[0].searchParams.get('cursor')).toBeNull();

    await fireEvent(screen.getByTestId('library-grid'), 'endReached');
    await waitFor(() => expect(listRequests().some((u) => u.searchParams.get('cursor'))).toBe(true));

    requests = [];
    await fireEvent.press(screen.getByLabelText('Games'));
    await waitFor(() => expect(listRequests().length).toBeGreaterThan(0));
    expect(listRequests().every((u) => u.searchParams.get('kind') === 'game' && !u.searchParams.get('cursor'))).toBe(true);
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(screen.queryByText('The Matrix')).toBeNull();
  });

  it('sorts by the chosen option', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Sort'));
    await fireEvent.press(screen.getByLabelText('Title'));

    await waitFor(() => expect(listRequests().some((u) => u.searchParams.get('sort') === 'title')).toBe(true));
  });

  it('gives every button an accessibility label', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');
    for (const button of screen.getAllByRole('button')) {
      expect(button.props.accessibilityLabel).toBeTruthy();
    }
  });

  it('keeps the grid inside the safe area', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.getByTestId('library-safe-area')).toBeTruthy();
  });
});

describe('search placeholder', () => {
  it.each([
    [undefined, 'Search your library'],
    [0, 'Search your 0 items'],
    [1, 'Search your 1 item'],
    [42, 'Search your 42 items'],
  ])('reads naturally with %s items', (total, expected) => {
    expect(searchPlaceholder(total)).toBe(expected);
  });
});

describe('content title', () => {
  it('shows the content title by default', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('Library')).toBeTruthy();
  });

  it('hides the content title when the stack header already names the page', async () => {
    await renderWithProviders(<LibraryView title="Wishlist" fixedStatus="wishlist" hideTitle />);

    expect(await screen.findByText('Blade Runner')).toBeTruthy();
    expect(screen.queryByText('Wishlist')).toBeNull();
  });
});

describe('Add item', () => {
  it('adds a catalog match straight from search', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Add item'));
    await fireEvent.changeText(screen.getByLabelText('Search the catalog'), 'alien');
    // Alien has a DVD and a VHS release; pick the DVD.
    await fireEvent.press(await screen.findByTestId('result-f1000000-0000-4000-8000-000000000001'));

    await waitFor(() => expect(screen.queryByText('Add an item')).toBeNull());
    await fireEvent.changeText(screen.getByLabelText('Search'), 'alien');
    expect(await screen.findByText('Alien')).toBeTruthy();
  });

  it('requires a title for a manual item, then shows the new item in the grid', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Add item'));
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));
    await fireEvent.press(screen.getByLabelText('Add to library'));
    expect(await screen.findByText('Give it a title.')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Kind Games'));
    await fireEvent.changeText(screen.getByLabelText('Title'), 'Zelda homebrew');
    await fireEvent.changeText(screen.getByLabelText('Year'), '2001');
    await fireEvent.press(screen.getByLabelText('Format Cartridge'));
    await fireEvent.press(screen.getByLabelText('Add to library'));

    await waitFor(() => expect(screen.queryByText('Add an item')).toBeNull());
    // Newest first, so the new item is on the first page.
    expect(await screen.findByText('Zelda homebrew')).toBeTruthy();
  });
});

describe('Library castle frame', () => {
  beforeEach(() => {
    setCastleStorage(memoryStore());
    useCastleThemeSetting.setState({ setting: 'full' });
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('frames the library in a battlement header with the count and the perched dragon', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getByRole('header', { name: 'Library' })).toBeTruthy();
    expect(screen.getByText('35 items')).toBeTruthy();
    expect(screen.getByTestId('castle-perch')).toBeTruthy();
  });

  it('leaves the perch empty when dragons are off, keeping the header', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getByRole('header', { name: 'Library' })).toBeTruthy();
    expect(screen.queryByTestId('castle-perch')).toBeNull();
  });

  it('flies a media swatch on every kind chip but All and Other', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    // Books, Games, Movies and TV; All has no medium and Other stays iron.
    expect(screen.getAllByTestId('chip-swatch')).toHaveLength(4);
  });

  it('seats every cover on a stone shelf', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getAllByTestId('cover-shelf').length).toBeGreaterThan(0);
  });

  it('poses the empty-hoard dragon when the library has no items', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items: [], nextCursor: null })));
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
    const dragon = within(screen.getByTestId('castle-state-dragon'));
    expect(dragon.queryByTestId('mascot-layer-hoard', { includeHiddenElements: true })).toBeNull();
    expect(dragon.getByTestId('mascot-layer-head', { includeHiddenElements: true })).toBeTruthy();
  });

  it('poses the fast-reading dragon while the list loads', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('*/api/v1/libraries/:libraryId/items', async () => {
        await gate;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    await renderWithProviders(<LibraryView title="Library" />);

    expect(screen.getByText('Loading your library…')).toBeTruthy();
    expect(within(screen.getByTestId('castle-state-dragon')).getByTestId('mascot-layer-page', { includeHiddenElements: true })).toBeTruthy();

    release();
    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
  });

  it('poses the oops dragon when the list fails to load', async () => {
    server.use(
      http.get('*/api/v1/libraries/:libraryId/items', () =>
        HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404, headers: { 'Content-Type': 'application/problem+json' } }),
      ),
    );
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText("Couldn't load your library")).toBeTruthy();
    const dragon = within(screen.getByTestId('castle-state-dragon'));
    expect(dragon.getByTestId('mascot-puff-0', { includeHiddenElements: true })).toBeTruthy();
    expect(dragon.queryByTestId('mascot-puff-1', { includeHiddenElements: true })).toBeNull();
  });

  it('keeps filtered no-matches plain: search results take no theme beyond the chrome', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.changeText(screen.getByLabelText('Search'), 'zzzz');

    expect(await screen.findByText('Nothing matches.')).toBeTruthy();
    expect(screen.queryByTestId('castle-state-dragon')).toBeNull();
  });

  it('skips the battlement where the stack header already names the page', async () => {
    await renderWithProviders(<LibraryView title="Wishlist" fixedStatus="wishlist" hideTitle />);

    expect(await screen.findByText('Blade Runner')).toBeTruthy();
    expect(screen.queryByTestId('castle-perch')).toBeNull();
    // The chips and shelves still carry the media colors.
    expect(screen.getAllByTestId('chip-swatch').length).toBeGreaterThan(0);
    expect(screen.getAllByTestId('cover-shelf').length).toBeGreaterThan(0);
  });
});
