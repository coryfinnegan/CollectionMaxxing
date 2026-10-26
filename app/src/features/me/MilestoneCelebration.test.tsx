// The milestone celebration (#362): a one-shot Lottie check with the shield
// dragon when the shelves climb a rung. Dragon Off removes it entirely.
import { act, fireEvent, render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { setMascotStorage, useDragonAppearance } from '@/ui/mascot/appearances';
import { MILESTONE_CELEBRATION_MS, MilestoneCelebration } from './MilestoneCelebration';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

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
});

describe('MilestoneCelebration', () => {
  it('names the new rung with the check and the shield dragon', async () => {
    reduceMotion(false);
    await render(<MilestoneCelebration rung={50} onDone={jest.fn()} />);

    expect(screen.getByLabelText('A new milestone. 50 items shelved. Dismiss.')).toBeTruthy();
    expect(screen.getByTestId('milestone-spot', { includeHiddenElements: true })).toBeTruthy();
    expect(screen.getByTestId('milestone-dragon')).toBeTruthy();
  });

  it('dismisses on a tap, never trapping input', async () => {
    reduceMotion(false);
    const onDone = jest.fn();
    const { unmount } = await render(<MilestoneCelebration rung={50} onDone={onDone} />);
    await act(async () => {
      fireEvent.press(screen.getByTestId('milestone-celebration'));
    });
    expect(onDone).toHaveBeenCalledTimes(1);
    unmount();
  });

  it('goes away by itself after a short moment', async () => {
    jest.useFakeTimers();
    try {
      reduceMotion(false);
      const onDone = jest.fn();
      const { unmount } = await render(<MilestoneCelebration rung={50} onDone={onDone} />);
      await act(async () => {
        jest.advanceTimersByTime(MILESTONE_CELEBRATION_MS);
      });
      expect(onDone).toHaveBeenCalledTimes(1);
      unmount();
    } finally {
      jest.useRealTimers();
    }
  });

  it('renders nothing when Reduce Motion is on; the shields tell the story', async () => {
    reduceMotion(true);
    await render(<MilestoneCelebration rung={50} onDone={jest.fn()} />);

    expect(screen.queryByTestId('milestone-celebration')).toBeNull();
  });

  it('renders nothing when dragons are off', async () => {
    reduceMotion(false);
    useDragonAppearance.setState({ appearance: 'off' });
    await render(<MilestoneCelebration rung={50} onDone={jest.fn()} />);

    expect(screen.queryByTestId('milestone-celebration')).toBeNull();
    useDragonAppearance.setState({ appearance: 'full' });
  });
});
