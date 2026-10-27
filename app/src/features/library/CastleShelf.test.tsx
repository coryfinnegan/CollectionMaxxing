// The library as castle shelves (#357): same items, same order, stone ledges.
import { fireEvent, screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { ItemSummaryDto } from '@/api/generated/models';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { setDesignStorage, useLibraryDesign } from './design';
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

function makeSummary(n: number, title?: string): ItemSummaryDto {
  const id = `b0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  return {
    id,
    releaseId: `e0000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    workId: `d0000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    title: title ?? `Shelf item ${n}`,
    kind: n % 2 === 0 ? 'book' : 'game',
    format: n % 2 === 0 ? 'paperback' : 'game_cartridge',
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

const five = [makeSummary(1, 'First'), makeSummary(2, 'Second'), makeSummary(3, 'Third'), makeSummary(4, 'Fourth'), makeSummary(5, 'Fifth')];

function serve(items: ItemSummaryDto[]) {
  server.use(http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items, nextCursor: null })));
}

const itemOrder = () => screen.getAllByTestId(/^item-/).map((node) => node.props.testID as string);

beforeEach(() => {
  resetItems();
  mockRouter.push.mockClear();
  setDesignStorage(memoryStore());
  useLibraryDesign.setState({ choice: 'castle' });
});

afterEach(() => {
  setDesignStorage(undefined);
  useLibraryDesign.setState({ choice: 'current' });
});

describe('the Castle shelf', () => {
  it('shelves a few items under banner markers', async () => {
    serve(five);
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Shelf 1')).toBeTruthy();
    expect(screen.getByTestId('shelf-1-marker')).toBeTruthy();
    expect(screen.getByText('First')).toBeTruthy();
    expect(screen.getByText('Fifth')).toBeTruthy();
  });

  it('keeps the grid order item for item', async () => {
    serve(five);
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('First');

    expect(itemOrder()).toEqual(five.map((item) => `item-${item.id}`));
  });

  it('opens an item when its book is pressed', async () => {
    serve(five);
    await renderWithProviders(<LibraryView title="Library" />);
    await fireEvent.press(await screen.findByLabelText('First, Cartridge'));

    expect(mockRouter.push).toHaveBeenCalledWith('/item/b0000000-0000-4000-8000-000000000001');
  });

  it('shows the empty-state copy when the library has no items', async () => {
    serve([]);
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
  });

  it('drops the markers once the shelf fills past twelve', async () => {
    serve(Array.from({ length: 13 }, (_, n) => makeSummary(n + 1)));
    await renderWithProviders(<LibraryView title="Library" />);

    expect(await screen.findByText('Shelf item 1')).toBeTruthy();
    expect(screen.queryByText('Shelf 1')).toBeNull();
  });
});
