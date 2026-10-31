import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockMe } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { localDateString } from '@/lib/dates';
import { renderWithProviders } from '@/test/render';
import { ItemDetailScreen } from './ItemDetailScreen';
import { ItemEditScreen } from './ItemEditScreen';
import { useUndoStore } from './useItem';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: jest.fn(() => true) };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  useLocalSearchParams: () => ({}),
}));

jest.mock('@react-native-community/datetimepicker', () => {
  const { View } = jest.requireActual('react-native');
  return { __esModule: true, default: () => <View testID="acquired-picker" /> };
});

const idOf = (title: string) => itemsState.entries.find((e) => e.work.title === title)!.item.id;
const entryOf = (title: string) => itemsState.entries.find((e) => e.work.title === title)!;

type Sent = { method: string; ifMatch: string | null; body: unknown };
let sent: Promise<Sent>[] = [];
const patches = () => Promise.all(sent);

beforeEach(() => {
  resetItems();
  useUndoStore.setState({ deleted: null });
  mockRouter.push.mockClear();
  sent = [];
  server.events.on('request:start', ({ request }) => {
    if (request.method === 'PATCH') {
      const clone = request.clone();
      sent.push(clone.json().then((body) => ({ method: request.method, ifMatch: request.headers.get('If-Match'), body })));
    }
  });
});
afterEach(() => server.events.removeAllListeners());

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), memberships: [{ ...mockMe().memberships[0]!, role }] })));

/** The item's screen, once it has loaded and its role is known. */
async function openItem(title: string) {
  const id = idOf(title);
  await renderWithProviders(<ItemDetailScreen id={id} />);
  await screen.findByText(`Status`);
  return id;
}

describe('moving an item between the library, the wishlist and sold', () => {
  it('offers I got it on a wishlist item, and one tap makes it owned today with an Undo toast', async () => {
    await openItem('Blade Runner');

    await fireEvent.press(await screen.findByLabelText('I got it'));

    expect(await screen.findByText('In your library')).toBeTruthy();
    const [patch] = await patches();
    expect(patch.ifMatch).toBe('"v1"');
    expect(patch.body).toEqual({ status: 'owned', acquiredOn: localDateString() });
    expect(entryOf('Blade Runner').item.status).toBe('owned');
    // It is owned now, so the screen offers what an owned item offers.
    expect(await screen.findByLabelText('Mark as missing')).toBeTruthy();
    expect(screen.queryByLabelText('I got it')).toBeNull();
  });

  it('puts a wishlist item back on the wishlist, as it was, when Undo is pressed', async () => {
    await openItem('Blade Runner');
    await fireEvent.press(await screen.findByLabelText('I got it'));
    await fireEvent.press(await screen.findByLabelText('Undo'));

    await waitFor(() => expect(entryOf('Blade Runner').item.status).toBe('wishlist'));
    const [, undo] = await patches();
    expect(undo.body).toEqual({ status: 'wishlist', acquiredOn: null });
    expect(entryOf('Blade Runner').item.acquiredOn).toBeNull();
    await waitFor(() => expect(screen.queryByText('In your library')).toBeNull());
  });

  it('moves an owned item to Sold, with a toast that says so and an Undo that brings it back', async () => {
    await openItem('Dune');

    await fireEvent.press(await screen.findByLabelText('Sold or gave it away'));

    expect(await screen.findByText('Moved to Sold')).toBeTruthy();
    expect((await patches())[0].body).toEqual({ status: 'sold' });
    expect(entryOf('Dune').item.status).toBe('sold');

    await fireEvent.press(screen.getByLabelText('Undo'));
    await waitFor(() => expect(entryOf('Dune').item.status).toBe('owned'));
    expect((await patches())[1].body).toEqual({ status: 'owned' });
  });

  it('marks an owned item as missing, and a missing item can be found again', async () => {
    await openItem('Dune');
    await fireEvent.press(await screen.findByLabelText('Mark as missing'));
    expect(await screen.findByText('Marked as missing')).toBeTruthy();
    expect((await patches())[0].body).toEqual({ status: 'missing' });

    await fireEvent.press(await screen.findByLabelText('Found it'));
    await waitFor(() => expect(entryOf('Dune').item.status).toBe('owned'));
    expect((await patches())[1].body).toEqual({ status: 'owned' });
  });

  it('gets a sold item back with Got it back', async () => {
    await openItem('Tetris');

    await fireEvent.press(await screen.findByLabelText('Got it back'));

    expect(await screen.findByText('Back in your library')).toBeTruthy();
    expect((await patches())[0].body).toEqual({ status: 'owned' });
    expect(entryOf('Tetris').item.status).toBe('owned');
  });

  it('gets a lent item back with Got it back, and Undo lends it to the same person again', async () => {
    await openItem('GoldenEye 007');

    await fireEvent.press(await screen.findByLabelText('Got it back'));

    await waitFor(() => expect(entryOf('GoldenEye 007').item.status).toBe('owned'));
    expect(entryOf('GoldenEye 007').item.lentTo).toBeNull(); // the server clears the borrower
    expect((await patches())[0].body).toEqual({ status: 'owned' });

    await fireEvent.press(await screen.findByLabelText('Undo'));
    await waitFor(() => expect(entryOf('GoldenEye 007').item.status).toBe('lent'));
    expect((await patches())[1].body).toEqual({ status: 'lent', lentTo: 'Sam', lentOn: '2026-09-20' });
    expect(entryOf('GoldenEye 007').item.lentTo).toBe('Sam');
  });

  it('opens the existing lend form from Lend… on an owned item', async () => {
    const id = await openItem('Dune');

    await fireEvent.press(await screen.findByLabelText('Lend…'));

    expect(mockRouter.push).toHaveBeenCalledWith({ pathname: '/item/[id]/edit', params: { id, status: 'lent' } });
  });

  it('shows the moves that fit and no others', async () => {
    const offered = async (title: string) => {
      const view = await renderWithProviders(<ItemDetailScreen id={idOf(title)} />);
      await screen.findByText('Status');
      const names = ['I got it', 'Found it', 'Got it back', 'Lend…', 'Mark as missing', 'Sold or gave it away'].filter(
        (name) => screen.queryByLabelText(name) !== null,
      );
      await view.unmount();
      return names;
    };

    expect(await offered('Blade Runner')).toEqual(['I got it']);
    expect(await offered('Dune')).toEqual(['Lend…', 'Mark as missing', 'Sold or gave it away']);
    expect(await offered('Pong')).toEqual(['Found it']);
    expect(await offered('Tetris')).toEqual(['Got it back']);
    expect(await offered('GoldenEye 007')).toEqual(['Got it back']);
  });

  it('shows a viewer none of the moves: they would be refused', async () => {
    asRole('viewer');
    for (const title of ['Blade Runner', 'Dune', 'Pong', 'Tetris', 'GoldenEye 007']) {
      const view = await renderWithProviders(<ItemDetailScreen id={idOf(title)} />);
      await screen.findByText('Status');
      await waitFor(() => expect(screen.queryByLabelText('Edit')).toBeNull());
      for (const name of ['I got it', 'Found it', 'Got it back', 'Lend…', 'Mark as missing', 'Sold or gave it away']) {
        expect(screen.queryByLabelText(name)).toBeNull();
      }
      await view.unmount();
    }
  });

  it('says why an Undo was refused when someone changed the item since', async () => {
    await openItem('Blade Runner');
    await fireEvent.press(await screen.findByLabelText('I got it'));
    await screen.findByText('In your library');
    // Someone else saves the item before Undo is pressed.
    entryOf('Blade Runner').version += 1;

    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByText("Couldn't undo. It changed since, so it stays as it is.")).toBeTruthy();
    expect(entryOf('Blade Runner').item.status).toBe('owned');
  });
});

describe('the other copies of a work, on the item screen', () => {
  /** Another copy of the item's work (same work and release) with this status. */
  const addCopy = (title: string, id: string, status: 'owned' | 'wishlist' | 'sold') => {
    const copy = JSON.parse(JSON.stringify(entryOf(title))) as ReturnType<typeof entryOf>;
    copy.item.id = id;
    copy.item.status = status;
    itemsState.entries.push(copy);
  };

  it('does not count a wishlisted or sold copy of the same work as a copy in your library', async () => {
    addCopy('Dune', 'f0000000-0000-4000-8000-000000000001', 'wishlist');
    addCopy('Dune', 'f0000000-0000-4000-8000-000000000002', 'sold');

    await openItem('Dune');

    expect(screen.queryByText(/copies of this work/)).toBeNull();
    expect(screen.queryByText('Also in your library')).toBeNull();
  });

  it('still lists another owned copy, and counts it', async () => {
    addCopy('Dune', 'f0000000-0000-4000-8000-000000000003', 'owned');
    addCopy('Dune', 'f0000000-0000-4000-8000-000000000004', 'wishlist');

    await openItem('Dune');

    expect(await screen.findByText('2 copies of this work')).toBeTruthy();
    expect(screen.getByText('Also in your library')).toBeTruthy();
  });
});

describe('the status chips in the edit form', () => {
  it('lists the three in the library first, a separator, then Wishlist and Sold', async () => {
    await renderWithProviders(<ItemEditScreen id={idOf('Dune')} />);
    await screen.findByLabelText('Status Owned');

    const chips = screen.getAllByLabelText(/^Status /).map((c) => c.props.accessibilityLabel);
    expect(chips).toEqual(['Status Owned', 'Status Lent out', 'Status Missing', 'Status Wishlist', 'Status Sold']);
    expect(screen.getByTestId('status-separator', { includeHiddenElements: true })).toBeTruthy();
  });

  it('still lets a status be set directly', async () => {
    await renderWithProviders(<ItemEditScreen id={idOf('Dune')} />);
    await fireEvent.press(await screen.findByLabelText('Status Sold'));
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(async () => expect((await patches())[0]?.body).toEqual({ status: 'sold' }));
  });

  it('opens as Lent out, asking who has it, when it is started from Lend…', async () => {
    await renderWithProviders(<ItemEditScreen id={idOf('Dune')} startAs="lent" />);

    expect(await screen.findByLabelText('Lent to')).toBeTruthy();
    expect(screen.getByLabelText('Status Lent out').props.accessibilityState.selected).toBe(true);
  });
});
