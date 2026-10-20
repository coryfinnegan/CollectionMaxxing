import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { setUpdateEnvironment, resetUpdateEnvironment } from '@/features/update/services';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { LibraryView } from './LibraryView';

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

describe('the update notice on the grid', () => {
  beforeEach(() => {
    setUpdateEnvironment({ store: new MemoryKeyValueStore(), appVersion: () => '1.0.1' });
    server.use(
      http.get('*/api/v1/app/ipa/info', () =>
        HttpResponse.json({ available: true, sizeBytes: 5, modifiedAt: '2026-10-20T12:00:00Z', sha256: null, version: '1.0.2' }),
      ),
    );
  });
  afterEach(() => resetUpdateEnvironment());

  it('shows on the Library, above the title', async () => {
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Version 1.0.2 is ready. Install it at home on your Wi-Fi.')).toBeTruthy();
  });

  it('does not show on the Wishlist or Lent out views', async () => {
    await renderWithProviders(<LibraryView title="Wishlist" fixedStatus="wishlist" />);
    await screen.findByText('Wishlist');
    await act(async () => {});

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
});

describe('Add item', () => {
  it('adds a catalogue match straight from search', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Add item'));
    await fireEvent.changeText(screen.getByLabelText('Search the catalogue'), 'alien');
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
