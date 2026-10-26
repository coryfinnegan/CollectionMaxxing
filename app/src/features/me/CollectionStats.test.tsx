// The Great Hall (#306, #363): the shape of the shelves from existing API data,
// mono numerals, milestone arms, series shields and a share card, no new endpoints.
import { fireEvent, screen, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { StyleSheet } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { typeScale } from '@/theme/tokens';
import { CollectionStats } from './CollectionStats';
import { shareMilestone } from './shareMilestone';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));
jest.mock('./shareMilestone', () => ({ shareMilestone: jest.fn() }));
const shared = shareMilestone as jest.Mock;

beforeEach(() => mockRouter.push.mockClear());

const stats = async () => {
  await renderWithProviders(<CollectionStats libraryId={MOCK_LIBRARY_ID} />);
  await screen.findByText('Great hall');
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

    await renderWithProviders(<CollectionStats libraryId={MOCK_LIBRARY_ID} />);

    expect(await screen.findByText('Nothing here yet.')).toBeTruthy();
    expect(screen.queryByText('Newest')).toBeNull();
  });
});

describe('GreatHall milestones', () => {
  beforeEach(() => shared.mockReset().mockResolvedValue('shared'));

  it('raises the earned arms and points at the next rung', async () => {
    await stats();

    expect(screen.getByText('Milestones')).toBeTruthy();
    expect(within(screen.getByTestId('milestone-10')).getByText('10 items')).toBeTruthy();
    expect(within(screen.getByTestId('milestone-10')).getByText('Whole collection')).toBeTruthy();
    expect(within(screen.getByTestId('milestone-book')).getByText('Books')).toBeTruthy();
    expect(within(screen.getByTestId('milestone-book')).getByText('10 shelved')).toBeTruthy();
    expect(screen.getByText('15 more to reach 50.')).toBeTruthy();
    expect(screen.queryByTestId('milestone-50')).toBeNull();
  });

  it('shares the top milestone', async () => {
    await stats();

    expect(screen.getByText('You shelved 10 items')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Share this milestone'));

    expect(shared).toHaveBeenCalledWith('I shelved 10 items in my CollectionMaxxing library.');
  });

  it('says when the milestone lands on the clipboard instead', async () => {
    shared.mockResolvedValue('copied');
    await stats();

    await fireEvent.press(screen.getByLabelText('Share this milestone'));

    expect(await screen.findByText('Copied to the clipboard.')).toBeTruthy();
  });

  it('says when sharing is not available', async () => {
    shared.mockResolvedValue('unavailable');
    await stats();

    await fireEvent.press(screen.getByLabelText('Share this milestone'));

    expect(await screen.findByText('Sharing is not available here.')).toBeTruthy();
  });

  it('shields the works shelved more than once', async () => {
    const matrix = itemsState.entries[0];
    itemsState.entries.push({ ...matrix, item: { ...matrix.item, id: 'c0000000-0000-4000-8000-999999999999' } });
    try {
      await stats();

      expect(await screen.findByText('Shelved more than once')).toBeTruthy();
      const shield = within(screen.getByTestId(`series-${matrix.work.id}`));
      expect(shield.getByText('The Matrix')).toBeTruthy();
      expect(shield.getByText('2 copies')).toBeTruthy();
    } finally {
      resetItems();
    }
  });
});
