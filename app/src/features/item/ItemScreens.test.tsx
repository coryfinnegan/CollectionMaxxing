import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import DetailRoute from '@/app/item/[id]/index';
import EditRoute from '@/app/item/[id]/edit';
import { LibraryView } from '@/features/library/LibraryView';
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

  it('shows friendly copy when the item is gone', async () => {
    await renderWithProviders(<ItemDetailScreen id="missing" />);
    expect(await screen.findByText("Couldn't load this item")).toBeTruthy();
    expect(screen.getByText("That's no longer here. Refresh and try again.")).toBeTruthy();
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
