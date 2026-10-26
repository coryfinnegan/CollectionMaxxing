import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import DetailRoute from '@/app/item/[id]/index';
import EditRoute from '@/app/item/[id]/edit';
import { LibraryView } from '@/features/library/LibraryView';
import { boundScanStore } from '@/features/review/boundScan';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { ItemDetailScreen } from './ItemDetailScreen';
import { ItemEditScreen } from './ItemEditScreen';
import { useUndoStore } from './useItem';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  useLocalSearchParams: () => ({ id: 'c0000000-0000-4000-8000-000000000001' }),
}));

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const entry = () => itemsState.entries.find((e) => e.item.id === MATRIX)!;

let patchHeaders: (string | null)[] = [];

beforeEach(() => {
  resetItems();
  boundScanStore.getState().clear();
  useUndoStore.setState({ deleted: null });
  mockRouter.back.mockClear();
  mockRouter.push.mockClear();
  patchHeaders = [];
  server.events.on('request:start', ({ request }) => {
    if (request.method === 'PATCH') patchHeaders.push(request.headers.get('If-Match'));
  });
});
afterEach(() => server.events.removeAllListeners());

describe('ItemDetailScreen', () => {
  it('shows the facts, the location breadcrumb and the other copies', async () => {
    entry().item.locationId = 'a0000000-0000-4000-8000-000000000003';
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(await screen.findByText('Game room › Shelf 2')).toBeTruthy();
    expect(screen.getByText('Owned')).toBeTruthy();
  });

  it('moves the item to another location', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Move'));
    await fireEvent.press(await screen.findByLabelText('Shelf 1'));

    await waitFor(() => expect(entry().item.locationId).toBe('a0000000-0000-4000-8000-000000000002'));
    expect(patchHeaders).toEqual(['"v1"']);
  });

  it('badges an edition that is still unverified, and stays quiet once it is proven', async () => {
    entry().item.editionVerified = false;
    const unverified = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('Edition unverified')).toBeTruthy();
    await unverified.unmount();

    entry().item.editionVerified = true;
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(screen.queryByText('Edition unverified')).toBeNull();
  });

  it('offers a barcode scan while the edition is unverified, and stays quiet once it is proven', async () => {
    entry().item.editionVerified = false;
    const unverified = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    await fireEvent.press(await screen.findByLabelText('Scan barcode to verify edition'));
    expect(boundScanStore.getState().binding).toEqual({
      libraryId: MOCK_LIBRARY_ID,
      itemId: MATRIX,
      title: 'The Matrix',
      kind: 'itemBarcode',
    });
    expect(mockRouter.push).toHaveBeenCalledWith('/scanner');
    await unverified.unmount();

    entry().item.editionVerified = true;
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);

    expect(await screen.findByText('The Matrix')).toBeTruthy();
    expect(screen.queryByLabelText('Scan barcode to verify edition')).toBeNull();
  });

  it('shows friendly copy when the item is gone', async () => {
    await renderWithProviders(<ItemDetailScreen id="missing" />);
    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.getByText("That's no longer here. Refresh and try again.")).toBeTruthy();
  });

  it('sets the notes on a parchment card', async () => {
    entry().item.notes = 'Slipcover missing';
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('Notes')).toBeTruthy();
    expect(screen.getByText('Slipcover missing')).toBeTruthy();
  });

  it('says none on the notes card when there are no notes', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByText('Notes')).toBeTruthy();
    expect(screen.getByText('None')).toBeTruthy();
  });

  it('shields a work owned more than once', async () => {
    const matrix = entry();
    itemsState.entries.push({ ...matrix, item: { ...matrix.item, id: 'c0000000-0000-4000-8000-999999999999' } });
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.getByTestId('series-shield')).toBeTruthy();
    expect(screen.getByText('2 copies of this work')).toBeTruthy();
  });

  it('shows no shield for a single copy', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(screen.queryByTestId('series-shield')).toBeNull();
  });

  it('poses the oops dragon when the item is gone', async () => {
    await renderWithProviders(<ItemDetailScreen id="missing" />);
    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.getByTestId('castle-state-dragon')).toBeTruthy();
  });

  it('deletes the item and goes back', async () => {
    await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Delete'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.deletedAt).not.toBeNull();
    expect(useUndoStore.getState().deleted?.title).toBe('The Matrix');
  });

  it('Undo after a delete brings the item back to the list', async () => {
    const detail = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');
    await fireEvent.press(screen.getByLabelText('Delete'));
    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    await detail.unmount();

    await renderWithProviders(<LibraryView title="Library" />);
    expect(await screen.findByText('Deleted The Matrix')).toBeTruthy();
    expect(screen.queryByLabelText('The Matrix, DVD')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByLabelText('The Matrix, DVD')).toBeTruthy();
    expect(entry().item.deletedAt).toBeNull();
    expect(screen.queryByText('Deleted The Matrix')).toBeNull();
  });
});

describe('ItemEditScreen', () => {
  it('saves with If-Match from the loaded ETag', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Condition Mint'));
    await fireEvent.changeText(screen.getByLabelText('Notes'), 'Slipcover missing');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(patchHeaders).toEqual(['"v1"']);
    expect(entry().item.notes).toBe('Slipcover missing');
    expect(entry().item.condition).toBe('mint');
  });

  it('after a stale ETag, shows the reload message and keeps the edits visible', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    // Someone else saves first.
    entry().version += 1;

    await fireEvent.changeText(screen.getByLabelText('Notes'), 'My edit');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    expect(await screen.findByText('Someone changed this. Reload?')).toBeTruthy();
    expect(screen.getByDisplayValue('My edit')).toBeTruthy();
    expect(mockRouter.back).not.toHaveBeenCalled();
    expect(entry().item.notes).toBeNull();

    // Reload takes the latest version, and saving then works.
    await fireEvent.press(screen.getByLabelText('Reload'));
    await waitFor(() => expect(screen.queryByText('Someone changed this. Reload?')).toBeNull());
    await fireEvent.changeText(screen.getByLabelText('Notes'), 'My edit');
    await fireEvent.press(screen.getByLabelText('Save changes'));
    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(patchHeaders).toEqual(['"v1"', '"v2"']);
  });

  it('rejects a date that is not YYYY-MM-DD without calling the server', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.changeText(screen.getByLabelText('Acquired'), 'June 2');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    expect(await screen.findByText('Use the format 2024-06-02.')).toBeTruthy();
    expect(patchHeaders).toEqual([]);
  });

  it('asks who an item is lent to when its status is Lent out', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Status Lent out'));
    await fireEvent.changeText(screen.getByLabelText('Lent to'), 'Sam');
    await fireEvent.press(screen.getByLabelText('Save changes'));

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.status).toBe('lent');
    expect(entry().item.lentTo).toBe('Sam');
  });

  it('records the local day when an item is lent in the evening', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');

    await fireEvent.press(screen.getByLabelText('Status Lent out'));
    await fireEvent.changeText(screen.getByLabelText('Lent to'), 'Sam');

    // 9:30pm Oct 24 in US Central, already Oct 25 in UTC. Only `new Date()` with no
    // arguments is fixed; dated constructions and every static pass straight through.
    // (Timezone comes from the gate's TZ=America/Chicago run; setting process.env.TZ
    // here would be too late.)
    const RealDate = globalThis.Date;
    const evening = RealDate.parse('2026-10-25T02:30:00Z');
    globalThis.Date = new Proxy(RealDate, {
      construct: (target, args) => (args.length === 0 ? new target(evening) : Reflect.construct(target, args)),
    });
    try {
      await fireEvent.press(screen.getByLabelText('Save changes'));
    } finally {
      globalThis.Date = RealDate;
    }

    await waitFor(() => expect(mockRouter.back).toHaveBeenCalled());
    expect(entry().item.lentOn).toBe('2026-10-24');
  });

  it('gives every button an accessibility label', async () => {
    await renderWithProviders(<ItemEditScreen id={MATRIX} />);
    await screen.findByLabelText('Notes');
    for (const button of screen.getAllByRole('button')) {
      expect(button.props.accessibilityLabel).toBeTruthy();
    }
  });
});

describe('item routes', () => {
  it('keeps the detail screen inside the safe area', async () => {
    await renderWithProviders(<DetailRoute />);
    await screen.findByText('The Matrix');
    expect(screen.getByTestId('item-detail-safe-area')).toBeTruthy();
  });

  it('keeps the edit screen inside the safe area', async () => {
    await renderWithProviders(<EditRoute />);
    await screen.findByLabelText('Notes');
    expect(screen.getByTestId('item-edit-safe-area')).toBeTruthy();
  });
});
