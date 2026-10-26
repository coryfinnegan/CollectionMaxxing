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
import { seasonFor, seasonNames } from '@/ui/castle';
import { setQuestBoardStorage, setQuestSeenStorage, useQuestBoard } from '@/ui/castle/quests';
import { setMascotStorage, useDragonAppearance } from '@/ui/mascot/appearances';
import { CollectionStats } from './CollectionStats';
import { setMilestoneSeenStorage } from './milestoneSeen';
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

describe('GreatHall delights', () => {
  function memoryStore() {
    const map = new Map<string, string>();
    return {
      getString: (key: string) => map.get(key),
      set: (key: string, value: string) => void map.set(key, value),
    };
  }

  beforeEach(() => {
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
    setMilestoneSeenStorage(memoryStore());
    setQuestBoardStorage(memoryStore());
    setQuestSeenStorage(memoryStore());
    useQuestBoard.setState({ pinned: false });
  });

  afterEach(() => {
    useDragonAppearance.setState({ appearance: 'full' });
    useQuestBoard.setState({ pinned: false });
  });

  it('hangs the seasonal banner with the season outside', async () => {
    await stats();

    expect(screen.getByTestId('seasonal-banner')).toBeTruthy();
    expect(screen.getByText(seasonNames[seasonFor(new Date())])).toBeTruthy();
    expect(screen.getByText('The banners change with the seasons.')).toBeTruthy();
  });

  it('invites pinning the quest board, then shows the quests', async () => {
    await stats();

    await screen.findByTestId('quest-board');
    await fireEvent.press(screen.getByLabelText('Pin the quest board'));

    expect(await screen.findByText('Catalog one full shelf')).toBeTruthy();
    expect(screen.getByText('Spread across three shelves')).toBeTruthy();
    expect(screen.getByText('Complete a series')).toBeTruthy();
    expect(screen.getByText('Quests wait as long as you like.')).toBeTruthy();
  });

  it('celebrates a climbed milestone rung once, then lets it rest', async () => {
    const seen = memoryStore();
    seen.set(`milestone-seen:${MOCK_LIBRARY_ID}`, '10');
    setMilestoneSeenStorage(seen);
    server.use(http.get('*/api/v1/libraries/:libraryId/stats', () => HttpResponse.json({ total: 55, byKind: { book: 55 }, byFormat: {}, byPlatform: {}, byDecade: {} })));

    await stats();

    expect(await screen.findByTestId('milestone-celebration')).toBeTruthy();
    expect(screen.getByLabelText('A new milestone. 50 items shelved. Dismiss.')).toBeTruthy();
    await fireEvent.press(screen.getByTestId('milestone-celebration'));
    expect(screen.queryByTestId('milestone-celebration')).toBeNull();
  });

  it('removes every delight when dragons are off', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    const seen = memoryStore();
    seen.set(`milestone-seen:${MOCK_LIBRARY_ID}`, '10');
    setMilestoneSeenStorage(seen);
    server.use(http.get('*/api/v1/libraries/:libraryId/stats', () => HttpResponse.json({ total: 55, byKind: { book: 55 }, byFormat: {}, byPlatform: {}, byDecade: {} })));

    await stats();

    expect(screen.queryByTestId('seasonal-banner')).toBeNull();
    expect(screen.queryByText('The banners change with the seasons.')).toBeNull();
    expect(screen.queryByTestId('quest-board')).toBeNull();
    expect(screen.queryByText('Quest board')).toBeNull();
    expect(screen.queryByTestId('milestone-celebration')).toBeNull();
    // The hall itself stands: only the delights go.
    expect(screen.getByText('Great hall')).toBeTruthy();
    expect(screen.getByText('Milestones')).toBeTruthy();
  });
});
