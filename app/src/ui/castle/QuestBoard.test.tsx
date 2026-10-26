// The quest board (#362): an opt-in pin, pinned parchment cards, and a one-shot
// celebration for newly finished quests. Dragon Off removes the whole board.
import { act, fireEvent, render, screen } from '@testing-library/react-native';
import { setMascotStorage, useDragonAppearance } from '../mascot/appearances';
import { QuestBoard } from './QuestBoard';
import { questsFor, setQuestBoardStorage, setQuestSeenStorage, useQuestBoard } from './quests';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

const empty = { fullestShelf: 0, shelvesUsed: 0, bestSet: 0 };

beforeEach(() => {
  setMascotStorage(memoryStore());
  useDragonAppearance.setState({ appearance: 'full' });
  setQuestBoardStorage(memoryStore());
  setQuestSeenStorage(memoryStore());
  useQuestBoard.setState({ pinned: false });
});

describe('QuestBoard', () => {
  it('renders nothing when dragons are off, pinned or not', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    const { rerender } = await render(<QuestBoard quests={questsFor(empty)} />);
    expect(screen.queryByTestId('quest-board')).toBeNull();

    useQuestBoard.setState({ pinned: true });
    await rerender(<QuestBoard quests={questsFor(empty)} />);
    expect(screen.queryByTestId('quest-board')).toBeNull();
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('invites pinning with the no-deadline promise', async () => {
    await render(<QuestBoard quests={questsFor(empty)} />);

    expect(screen.getByTestId('quest-board')).toBeTruthy();
    expect(screen.getByText('Three quiet quests, no deadlines.')).toBeTruthy();
    expect(screen.getByLabelText('Pin the quest board')).toBeTruthy();
    expect(screen.queryByText('Catalog one full shelf')).toBeNull();
  });

  it('pins parchment cards with plain progress lines', async () => {
    await render(<QuestBoard quests={questsFor({ fullestShelf: 7, shelvesUsed: 3, bestSet: 1 })} />);
    await act(async () => {
      fireEvent.press(screen.getByLabelText('Pin the quest board'));
    });

    expect(screen.getByText('Quests wait as long as you like.')).toBeTruthy();
    expect(screen.getByText('Catalog one full shelf')).toBeTruthy();
    expect(screen.getByText('Ten items on one shelf. 7 of 10.')).toBeTruthy();
    expect(screen.getByText('Spread across three shelves')).toBeTruthy();
    expect(screen.getByText('Items on three shelves. Done. It stays done.')).toBeTruthy();
    expect(useQuestBoard.getState().pinned).toBe(true);
  });

  it('takes the board down without losing the shelves', async () => {
    useQuestBoard.getState().set(true);
    await render(<QuestBoard quests={questsFor(empty)} />);
    await act(async () => {
      fireEvent.press(screen.getByLabelText('Take the board down'));
    });

    expect(screen.getByLabelText('Pin the quest board')).toBeTruthy();
    expect(useQuestBoard.getState().pinned).toBe(false);
  });

  it('celebrates a newly finished quest once, then lets it rest', async () => {
    jest.useFakeTimers();
    try {
      useQuestBoard.getState().set(true);
      const { rerender } = await render(<QuestBoard quests={questsFor(empty)} />);
      expect(screen.queryByTestId('quest-celebration')).toBeNull();

      await rerender(<QuestBoard quests={questsFor({ ...empty, fullestShelf: 10 })} />);
      expect(screen.getByTestId('quest-celebration')).toBeTruthy();
      expect(screen.getByLabelText('Done. Catalog one full shelf. Dismiss.')).toBeTruthy();

      await act(async () => {
        fireEvent.press(screen.getByTestId('quest-celebration'));
      });
      expect(screen.queryByTestId('quest-celebration')).toBeNull();

      await rerender(<QuestBoard quests={questsFor({ ...empty, fullestShelf: 10 })} />);
      expect(screen.queryByTestId('quest-celebration')).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('stays up on minimal; the board is content, not decoration', async () => {
    useDragonAppearance.setState({ appearance: 'minimal' });
    await render(<QuestBoard quests={questsFor(empty)} />);

    expect(screen.getByTestId('quest-board')).toBeTruthy();
    useDragonAppearance.setState({ appearance: 'full' });
  });
});
