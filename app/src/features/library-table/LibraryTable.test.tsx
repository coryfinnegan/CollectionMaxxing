import { configure, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
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

describe('LibraryTable', () => {
  it('shows each item with its format, platform and year', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(screen.getAllByText('Nintendo 64').length).toBeGreaterThan(0);
    expect(screen.getByLabelText('Open The Matrix')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Open The Matrix'));
    expect(mockRouter.push).toHaveBeenCalledWith(`/item/${ID(1)}`);
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
    await screen.findByText('The Matrix');

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
    await screen.findByText('The Matrix');
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
    await screen.findByText('The Matrix');
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
    await screen.findByText('The Matrix');
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

  it('deletes the selected items after confirmation', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    await fireEvent.press(screen.getByLabelText('Select The Matrix'));
    await fireEvent.press(screen.getByLabelText('Delete'));
    expect(await screen.findByText('Delete 1 item?')).toBeTruthy();
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
    await fireEvent.press(screen.getByLabelText('Select all loaded items'));

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
    await screen.findByText('The Matrix');
    await fireEvent.press(screen.getByLabelText('Change status of The Matrix, now Owned'));
    await fireEvent.press(await screen.findByLabelText('Set status of The Matrix to Wishlist'));

    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].headers.get('if-match')).toBe('"v1"');
    expect(patches()[0].body).toEqual({ status: 'wishlist' });
    await waitFor(() => expect(screen.getByLabelText('Change status of The Matrix, now Wishlist')).toBeTruthy());
  });

  it('changes a location inline', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    await fireEvent.press(screen.getByLabelText('Change location of Dune, now Shelf 2'));
    await fireEvent.press(await screen.findByLabelText('Set location of Dune to No location'));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].body).toEqual({ locationId: null });
  });

  it('reloads the row and explains when someone else changed the item (412)', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
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
    const matrix = itemsState.entries.find((e) => e.item.id === ID(1));
    if (!matrix) throw new Error('seed item missing');
    matrix.item.createdAt = '2026-10-25T02:30:00Z';
    store.set(STORAGE_KEY, JSON.stringify(['title', 'added']));

    const previousTz = process.env.TZ;
    process.env.TZ = 'America/Chicago';
    try {
      await renderWithProviders(<LibraryTable title="Library" />);
      expect(await screen.findByText('2026-10-24')).toBeTruthy();
      expect(screen.queryByText('2026-10-25')).toBeNull();
    } finally {
      if (previousTz === undefined) delete process.env.TZ;
      else process.env.TZ = previousTz;
    }
  });

  it('keeps the table in a box that scrolls sideways', async () => {
    await renderWithProviders(<LibraryTable title="Library" />);
    await screen.findByText('The Matrix');
    expect(screen.getByTestId('library-table-scroll').props.horizontal).toBe(true);
  });
});
