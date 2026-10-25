// The Me tab's stats block (#306): the shape of the shelves from existing API
// data, mono numerals, no new endpoints.
import { fireEvent, screen, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { StyleSheet } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { typeScale } from '@/theme/tokens';
import { CollectionStats } from './CollectionStats';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

beforeEach(() => mockRouter.push.mockClear());

const stats = async () => {
  await renderWithProviders(<CollectionStats libraryId={MOCK_LIBRARY_ID} />);
  await screen.findByText('Your shelves');
};

describe('CollectionStats', () => {
  it('counts items by type and format in mono numerals', async () => {
    await stats();

    const books = within(screen.getByTestId('stat-Books'));
    expect(books.getByText('Books')).toBeTruthy();
    expect(StyleSheet.flatten(books.getByText('29').props.style).fontFamily).toBe(typeScale.mono.fontFamily);
    expect(within(screen.getByTestId('stat-Paperback')).getByText('29')).toBeTruthy();
    expect(within(screen.getByTestId('stat-Cartridge')).getByText('2')).toBeTruthy();
  });

  it('lists the newest items first and opens them', async () => {
    await stats();

    expect(screen.getByText('Newest')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('The Matrix, DVD'));
    expect(mockRouter.push).toHaveBeenCalledWith('/item/c0000000-0000-4000-8000-000000000001');
  });

  it('names the fullest shelves and where most of it lives', async () => {
    await stats();

    expect(screen.getByText('Fullest shelves')).toBeTruthy();
    expect(within(screen.getByTestId('shelf-Game room')).getByText('28')).toBeTruthy();
    expect(screen.getByText('Most of it lives on Game room.')).toBeTruthy();
  });

  it('says the shelves are empty when there is nothing to count', async () => {
    server.use(
      http.get('*/api/v1/libraries/:libraryId/stats', () => HttpResponse.json({ total: 0, byKind: {}, byFormat: {}, byPlatform: {}, byDecade: {} })),
      http.get('*/api/v1/libraries/:libraryId/items', () => HttpResponse.json({ items: [], nextCursor: null })),
    );

    await stats();

    expect(screen.getByText('Nothing here yet.')).toBeTruthy();
    expect(screen.queryByText('Newest')).toBeNull();
  });
});
