// The Mall library design (#357): stores narrow the grid order, covers identify.
import { fireEvent, screen, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { ItemSummaryDto, WorkKind } from '@/api/generated/models';
import { setDesignStorage, useLibraryDesign } from '@/features/library/design';
import { LibraryView } from '@/features/library/LibraryView';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

function makeItem(n: number, kind: WorkKind, title: string): ItemSummaryDto {
  const id = `e0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  return {
    id,
    releaseId: id,
    workId: id,
    title,
    kind,
    format: kind === 'book' ? 'paperback' : kind === 'game' ? 'game_cartridge' : 'dvd',
    platform: null,
    year: 2000,
    status: 'owned',
    condition: null,
    locationId: null,
    coverUrl: null,
    createdAt: '2026-09-01T12:00:00Z',
    editionVerified: true,
  };
}

const mixed = [makeItem(1, 'film', 'Movie one'), makeItem(2, 'game', 'Game one'), makeItem(3, 'book', 'Book one')];

function serve(items: ItemSummaryDto[]) {
  server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items, nextCursor: null })));
}

const aisleOrder = () => screen.getAllByTestId(/^item-/).map((node) => node.props.testID as string);

beforeEach(() => {
  resetItems();
  mockRouter.push.mockClear();
  setDesignStorage(memoryStore());
  useLibraryDesign.setState({ choice: 'mall' });
});

afterEach(() => {
  setDesignStorage(undefined);
  useLibraryDesign.setState({ choice: 'current' });
});

describe('the Mall', () => {
  it('opens on the hub with every item in grid order', async () => {
    serve(mixed);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    expect(screen.getByLabelText('Room Mall').props.accessibilityState.selected).toBe(true);
    expect(aisleOrder()).toEqual(mixed.map((item) => `item-${item.id}`));
    // The room canvas and the aisle list expose the same items with the same labels.
    expect(screen.getAllByLabelText('Movie one, DVD')).toHaveLength(2);
  });

  it('narrows each store to its kinds', async () => {
    serve(mixed);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    await fireEvent.press(screen.getByLabelText('Room Games'));
    expect(aisleOrder()).toEqual([`item-${mixed[1].id}`]);

    await fireEvent.press(screen.getByLabelText('Room Movies'));
    expect(aisleOrder()).toEqual([`item-${mixed[0].id}`]);

    await fireEvent.press(screen.getByLabelText('Room Books'));
    expect(aisleOrder()).toEqual([`item-${mixed[2].id}`]);
  });

  it('identifies a tapped cover without leaving the room', async () => {
    serve(mixed);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    await fireEvent.press(screen.getByTestId(`cover-${mixed[1].id}`));

    const card = screen.getByTestId('mall-identify');
    expect(within(card).getByText('Game one')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Open item'));
    expect(mockRouter.push).toHaveBeenCalledWith(`/item/${mixed[1].id}`);
  });

  it('closes the identify card and clears it on room change', async () => {
    serve(mixed);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    await fireEvent.press(screen.getByTestId(`cover-${mixed[1].id}`));
    expect(screen.getByTestId('mall-identify')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Close'));
    expect(screen.queryByTestId('mall-identify')).toBeNull();

    await fireEvent.press(screen.getByTestId(`cover-${mixed[1].id}`));
    await fireEvent.press(screen.getByLabelText('Room Games'));
    expect(screen.queryByTestId('mall-identify')).toBeNull();
  });

  it('switches the camera presets', async () => {
    serve(mixed);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    expect(screen.getByLabelText('Camera Hero').props.accessibilityState.selected).toBe(true);

    await fireEvent.press(screen.getByLabelText('Camera Directory'));

    expect(screen.getByLabelText('Camera Directory').props.accessibilityState.selected).toBe(true);
  });

  it('says when the room has nothing, pointing at the other rooms', async () => {
    serve([mixed[2]]);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByTestId('mall-room');

    await fireEvent.press(screen.getByLabelText('Room Movies'));

    expect(await screen.findByText('Nothing in this room.')).toBeTruthy();
    expect(screen.getByText('Try another room.')).toBeTruthy();
  });

  it('shows the empty-state copy when the library has no items', async () => {
    serve([]);
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
  });
});
