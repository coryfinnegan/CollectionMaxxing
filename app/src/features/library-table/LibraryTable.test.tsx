import { act, configure, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { localDateString } from '@/lib/dates';
import { mockMe } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { STORAGE_KEY } from './columns';
import { LibraryTable } from './LibraryTable.web';

configure({ asyncUtilTimeout: 8000 });
jest.setTimeout(30_000);

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const store = new Map<string, string>();
const fakeStorage = {
  getItem: (key: string) => store.get(key) ?? null,
  setItem: (key: string, value: string) => void store.set(key, value),
  removeItem: (key: string) => void store.delete(key),
};
const install = (storage: unknown) =>
  Object.defineProperty(globalThis, 'localStorage', { value: storage, configurable: true, writable: true });

type Seen = { url: URL; method: string; headers: Headers; body?: unknown };
let seen: Seen[] = [];
const listRequests = () => seen.filter((r) => r.method === 'GET' && r.url.pathname.endsWith('/items'));
const bulkRequests = () => seen.filter((r) => r.url.pathname.endsWith('/items:bulk'));
const patches = () => seen.filter((r) => r.method === 'PATCH');

beforeEach(() => {
  resetItems();
  store.clear();
  install(fakeStorage);
  seen = [];
  server.events.on('request:start', ({ request }) => {
    const entry: Seen = { url: new URL(request.url), method: request.method, headers: request.headers };
    seen.push(entry);
    if (request.method !== 'GET') {
      void request.clone().json().then((body) => { entry.body = body; }).catch(() => undefined);
    }
  });
});
afterEach(() => server.events.removeAllListeners());
afterAll(() => install(undefined));

const ID = (n: number) => `c0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(
    http.get('*/api/v1/me', () =>
      HttpResponse.json({ ...mockMe(), memberships: [{ libraryId: mockMe().memberships[0]!.libraryId, role }] }),
    ),
  );

/** The rows have loaded and so has the caller's role: the selection column appears once an editor is known. */
const loaded = async () => {
  await screen.findByText('The Matrix');
  await screen.findByLabelText('Select all loaded items');
};

describe('LibraryTable', () => {
  it('switches to Needs attention from its chip, and back', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Needs attention'));
    expect(await screen.findByText('Nothing needs attention.')).toBeTruthy();
    expect(screen.queryByText('Super Mario 64')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Needs attention'));
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
  });

  it('shows each item with its format, platform and year', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(screen.getAllByText('Nintendo 64').length).toBeGreaterThan(0);
    expect(screen.getByLabelText('Open The Matrix')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Open The Matrix'));
    expect(mockRouter.push).toHaveBeenCalledWith(`/item/${ID(1)}`);
  });

  it('badges unverified editions and filters to just those', async () => {
    itemsState.entries.find((e) => e.work.title === 'The Matrix')!.item.editionVerified = false;
    await renderWithProviders(<LibraryTable title="Library" />);

    expect(await screen.findByLabelText('Open The Matrix, edition unverified')).toBeTruthy();
    expect(screen.getByLabelText('Open Super Mario 64')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Edition unverified'));
    await waitFor(() => expect(listRequests().some((r) => r.url.searchParams.get('editionVerified') === 'false')).toBe(true));
    // One wait for the settled list: the refetch clears the old page first, so
    // asserting absence alone can pass while both rows are still missing.
    await waitFor(() => {
      expect(screen.queryByText('Super Mario 64')).toBeNull();
      expect(screen.getByText('The Matrix')).toBeTruthy();
    });
  });

  it('hides and shows columns from the chooser and saves the choice', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.getByLabelText('Sort by Year')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Choose columns'));
    await fireEvent.press(screen.getByLabelText('Hide Year column'));
    expect(screen.queryByLabelText('Sort by Year')).toBeNull();
    expect(screen.getByLabelText('Show Year column')).toBeTruthy();
    expect(JSON.parse(store.get(STORAGE_KEY) ?? '[]')).not.toContain('year');

    await fireEvent.press(screen.getByLabelText('Show Year column'));
    expect(screen.getByLabelText('Sort by Year')).toBeTruthy();
  });

  it('opens with the columns saved before a reload', async () => {
    store.set(STORAGE_KEY, JSON.stringify(['title', 'format']));
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.queryByLabelText('Sort by Year')).toBeNull();
    expect(screen.queryByLabelText('Sort by Platform')).toBeNull();
    expect(screen.getByLabelText('Sort by Title')).toBeTruthy();
  });

  it('still works when localStorage is blocked', async () => {
    const blocked = () => {
      throw new Error('blocked');
    };
    install({ getItem: blocked, setItem: blocked, removeItem: blocked });
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    await fireEvent.press(screen.getByLabelText('Choose columns'));
    await fireEvent.press(screen.getByLabelText('Hide Year column'));
    expect(screen.queryByLabelText('Sort by Year')).toBeNull();
  });

  it('sorts with the matching sort param and goes back to the first page', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    await fireEvent.press(await screen.findByLabelText('Load more'));
    await waitFor(() => expect(listRequests().some((r) => r.url.searchParams.has('cursor'))).toBe(true));

    seen = [];
    await fireEvent.press(screen.getByLabelText('Sort by Year'));
    await waitFor(() => expect(listRequests().length).toBeGreaterThan(0));
    const request = listRequests()[0].url.searchParams;
    expect(request.get('sort')).toBe('year');
    expect(request.has('cursor')).toBe(false);
  });

  it('offers no sort button for columns the API cannot sort', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.queryByLabelText('Sort by Format')).toBeNull();
    expect(screen.queryByLabelText('Sort by Location')).toBeNull();
    expect(screen.queryByLabelText('Sort by Status')).toBeNull();
  });

  it('selects with click, shift-click and the header checkbox', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();

    await fireEvent.press(screen.getByTestId(`row-${ID(1)}`));
    expect(await screen.findByText('1 selected')).toBeTruthy();
    await fireEvent.press(screen.getByTestId(`row-${ID(4)}`), { shiftKey: true });
    expect(await screen.findByText('4 selected')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Select all loaded items'));
    expect(await screen.findByText('30 selected')).toBeTruthy();
    expect(screen.getByLabelText('Select all loaded items').props.accessibilityState.checked).toBe(true);
  });

  it('exposes rows as checkboxes with names that toggle selection', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    const box = screen.getByLabelText('Select The Matrix');
    expect(box.props.accessibilityRole).toBe('checkbox');
    expect(box.props.accessibilityState.checked).toBe(false);
    await fireEvent.press(box);
    expect(screen.getByLabelText('Select The Matrix').props.accessibilityState.checked).toBe(true);
    await fireEvent.press(screen.getByLabelText('Select Dune'));
    expect(await screen.findByText('2 selected')).toBeTruthy();
    for (const name of ['Move to', 'Set status', 'Add tag', 'Delete', 'Clear selection']) {
      expect(screen.getByLabelText(name)).toBeTruthy();
    }
  });

  it('moves exactly the selected items from the Move to action', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Select The Matrix'));
    await fireEvent.press(screen.getByLabelText('Select Dune'));
    await fireEvent.press(screen.getByLabelText('Move to'));
    await fireEvent.press(await screen.findByTestId('move-to-a0000000-0000-4000-8000-000000000002'));

    await waitFor(() => expect(bulkRequests()).toHaveLength(1));
    await waitFor(() => expect(bulkRequests()[0].body).toBeDefined());
    expect(bulkRequests()[0].body).toEqual({
      ids: [ID(1), ID(3)],
      action: 'patch',
      patch: { locationId: 'a0000000-0000-4000-8000-000000000002' },
    });
    expect(await screen.findByText('Moved 2 items.')).toBeTruthy();
    expect(screen.queryByText('2 selected')).toBeNull();
  });

  it('says in plain words when only some items were changed', async () => {
    server.use(http.post(/items:bulk$/, () => HttpResponse.json({ affected: 1 })));
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Select The Matrix'));
    await fireEvent.press(screen.getByLabelText('Select Dune'));
    await fireEvent.press(screen.getByLabelText('Set status'));
    await fireEvent.press(await screen.findByLabelText('Set status to Wishlist'));
    expect(
      await screen.findByText(
        "Updated 1 of 2 items. The other 1 were deleted or changed by someone else. Reload the list to see what's there now.",
      ),
    ).toBeTruthy();
  });

  it('deletes the selected items after confirmation, and says where to bring them back from', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Select The Matrix'));
    await fireEvent.press(screen.getByLabelText('Delete'));
    expect(await screen.findByText('Delete 1 item?')).toBeTruthy();
    // A deleted item's own page is gone; the way back is Me, then Recently deleted.
    expect(screen.getByText(/Me, then Recently deleted/)).toBeTruthy();
    expect(screen.queryByText(/from its page/)).toBeNull();
    expect(bulkRequests()).toHaveLength(0);
    await fireEvent.press(screen.getByLabelText('Delete 1 item'));
    expect(await screen.findByText('Deleted 1 item.')).toBeTruthy();
    expect(bulkRequests()[0].body).toEqual({ ids: [ID(1)], action: 'delete' });
  });

  it('caps a bulk action at 500 ids and says so', async () => {
    const many = Array.from({ length: 520 }, (_, i) => ({
      id: `b0000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
      releaseId: 'r', workId: 'w', title: `Item ${i}`, kind: 'book', format: 'paperback', platform: null, year: 2000,
      status: 'owned', condition: null, locationId: null, coverUrl: null, createdAt: '2026-09-01T12:00:00Z',
    }));
    server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items: many, nextCursor: null })));
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('Item 0');
    await fireEvent.press(await screen.findByLabelText('Select all loaded items'));

    expect(await screen.findByText('520 selected')).toBeTruthy();
    expect(screen.getByText('520 selected. Bulk actions change 500 at a time, so they apply to the first 500.')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Set status'));
    await fireEvent.press(await screen.findByLabelText('Set status to Missing'));

    await waitFor(() => expect(bulkRequests()).toHaveLength(1));
    await waitFor(() => expect(bulkRequests()[0].body).toBeDefined());
    expect((bulkRequests()[0].body as { ids: string[] }).ids).toHaveLength(500);
  });

  it('changes a status inline with If-Match', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Change status of The Matrix, now Owned'));
    await fireEvent.press(await screen.findByLabelText('Set status of The Matrix to Wishlist'));

    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].headers.get('if-match')).toBe('"v1"');
    // Not lent out, so any stale borrower is cleared along with the status.
    expect(patches()[0].body).toEqual({ status: 'wishlist', lentTo: null, lentOn: null });
    await waitFor(() => expect(screen.getByLabelText('Change status of The Matrix, now Wishlist')).toBeTruthy());
  });

  it('clears who it was lent to when an item leaves Lent out', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(await screen.findByLabelText('Change status of GoldenEye 007, now Lent out'));
    await fireEvent.press(await screen.findByLabelText('Set status of GoldenEye 007 to Owned'));

    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].body).toEqual({ status: 'owned', lentTo: null, lentOn: null });
    await waitFor(() => {
      const goldeneye = itemsState.entries.find((e) => e.work.title === 'GoldenEye 007')!.item;
      expect(goldeneye.status).toBe('owned');
      expect(goldeneye.lentTo).toBeNull();
      expect(goldeneye.lentOn).toBeNull();
    });
  });

  it('lends an item out with the name blank and today as the date, like the edit form', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Change status of The Matrix, now Owned'));
    await fireEvent.press(await screen.findByLabelText('Set status of The Matrix to Lent out'));

    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].body).toEqual({ status: 'lent', lentTo: null, lentOn: localDateString() });
  });

  it('changes a location inline', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Change location of Dune, now Shelf 2'));
    await fireEvent.press(await screen.findByLabelText('Set location of Dune to No location'));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].body).toEqual({ locationId: null });
  });

  it('reloads the row and explains when someone else changed the item (412)', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await loaded();
    await fireEvent.press(screen.getByLabelText('Change status of The Matrix, now Owned'));
    const option = await screen.findByLabelText('Set status of The Matrix to Wishlist');
    // Someone else edits the item after the editor opened.
    await waitFor(() => expect(seen.some((r) => r.url.pathname.endsWith(`/items/${ID(1)}`))).toBe(true));
    const entry = itemsState.entries.find((e) => e.item.id === ID(1));
    if (!entry) throw new Error('seed item missing');
    entry.item.status = 'lent';
    entry.version += 1;
    await fireEvent.press(option);

    expect(
      await screen.findByText(
        'Someone else changed "The Matrix" while you were editing. The row now shows the latest, so choose the status again.',
      ),
    ).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText('Change status of The Matrix, now Lent out')).toBeTruthy());
  });

  it('shows the local day in the Added column for an evening addition', async () => {
    // 9:30pm Oct 24 in US Central, already Oct 25 in UTC.
    // (Timezone comes from the gate's TZ=America/Chicago run; setting process.env.TZ
    // here would be too late.)
    const matrix = itemsState.entries.find((e) => e.item.id === ID(1));
    if (!matrix) throw new Error('seed item missing');
    matrix.item.createdAt = '2026-10-25T02:30:00Z';
    store.set(STORAGE_KEY, JSON.stringify(['title', 'added']));

    await renderWithProviders(<LibraryTable title="Library" />);
    expect(await screen.findByText('2026-10-24')).toBeTruthy();
    expect(screen.queryByText('2026-10-25')).toBeNull();
  });

  it('keeps the table in a box that scrolls sideways', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.getByTestId('library-table-scroll').props.horizontal).toBe(true);
  });
});

describe('LibraryTable sort arrow', () => {
  it('points up for an ascending sort and down for newest first', async () => {
    store.set(STORAGE_KEY, JSON.stringify(['title', 'year', 'added']));
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');

    // The list opens newest first, which is the Added column's sort.
    expect(screen.getByText('Added ↓')).toBeTruthy();
    expect(screen.queryByText(/Added ↑/)).toBeNull();

    await fireEvent.press(screen.getByLabelText('Sort by Title'));
    expect(await screen.findByText('Title ↑')).toBeTruthy();
    expect(screen.queryByText(/^Added ./)).toBeNull();
    expect(screen.getByText('Added')).toBeTruthy();
  });
});

describe('LibraryTable Add item', () => {
  it('has an Add item button in the header that opens the Add an item panel and closes it again', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Add item'));
    expect(await screen.findByText('Add an item')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Add item'));
    expect(screen.queryByText('Add an item')).toBeNull();
  });

  it('adds an item by hand and lists it in the table', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Add item'));
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));
    await fireEvent.press(screen.getByLabelText('Kind Games'));
    await fireEvent.changeText(screen.getByLabelText('Title'), 'Zelda homebrew');
    await fireEvent.changeText(screen.getByLabelText('Year'), '2001');
    await fireEvent.press(screen.getByLabelText('Format Cartridge'));
    await fireEvent.press(screen.getByLabelText('Add to library'));

    await waitFor(() => expect(screen.queryByText('Add an item')).toBeNull());
    expect(await screen.findByLabelText('Open Zelda homebrew')).toBeTruthy();
  });

  it.each(['owner', 'editor'] as const)('offers Add item to an %s', async (role) => {
    asRole(role);
    await renderWithProviders(<LibraryTable title="Library" />);
    expect(await screen.findByLabelText('Add item')).toBeTruthy();
  });
});

describe('LibraryTable for a viewer', () => {
  /** Waits until the role has arrived, so a missing control is the role's doing and not a slow answer. */
  const viewerLoaded = async () => {
    let askedWho = false;
    server.events.on('request:end', ({ request }) => {
      if (new URL(request.url).pathname.endsWith('/api/v1/me')) askedWho = true;
    });
    asRole('viewer');
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    await waitFor(() => expect(askedWho).toBe(true));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
  };

  it('shows no Add item, no selection boxes and no bulk actions', async () => {
    await viewerLoaded();

    expect(screen.queryByLabelText('Add item')).toBeNull();
    expect(screen.queryByLabelText('Select all loaded items')).toBeNull();
    expect(screen.queryByLabelText('Select The Matrix')).toBeNull();
    // Clicking a row selects nothing, so no bar of bulk actions can appear.
    await fireEvent.press(screen.getByTestId(`row-${ID(1)}`));
    expect(screen.queryByText(/selected$/)).toBeNull();
    for (const name of ['Move to', 'Set status', 'Add tag', 'Delete']) {
      expect(screen.queryByLabelText(name)).toBeNull();
    }
  });

  it('shows status and location as plain text, with nothing to change them', async () => {
    await viewerLoaded();

    expect(screen.queryByLabelText(/^Change status of/)).toBeNull();
    expect(screen.queryByLabelText(/^Change location of/)).toBeNull();
    expect(screen.getAllByText('Owned').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Living room').length).toBeGreaterThan(0);
    // Looking is still allowed.
    expect(screen.getByLabelText('Open The Matrix')).toBeTruthy();
    expect(screen.getByLabelText('Search')).toBeTruthy();
  });

  it('does not tell a viewer to use Add item on an empty shelf', async () => {
    server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items: [], nextCursor: null })));
    asRole('viewer');
    await renderWithProviders(<LibraryTable title="Library" />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText(/Use Add item/)).toBeNull());
    expect(await screen.findByText(/once an owner or editor adds them/)).toBeTruthy();
  });
});
