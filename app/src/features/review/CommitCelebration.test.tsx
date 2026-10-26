// The scan-success moment plays its check, then gets out of the way on its own or on a tap.
import { act, fireEvent, render, screen } from '@testing-library/react-native';
import successCheck from '@/assets/lottie/success-check.json';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { setMascotStorage, useDragonAppearance } from '@/ui/mascot/appearances';
import { CELEBRATION_MS, CELEBRATION_SPEED, CommitCelebration } from './CommitCelebration';

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

describe('CommitCelebration', () => {
  it('plays the success check with the commit message for screen readers', async () => {
    reduceMotion(false);
    await render(<CommitCelebration message="Added 3 items" onDone={jest.fn()} />);

    expect(screen.getByLabelText('Added 3 items. Dismiss.')).toBeTruthy();
    expect(screen.getByTestId('celebration-spot', { includeHiddenElements: true })).toBeTruthy();
  });

  it('dismisses on a tap, never trapping input', async () => {
    reduceMotion(false);
    const onDone = jest.fn();
    await render(<CommitCelebration message="Added 1 item" onDone={onDone} />);

    await act(async () => {
      fireEvent.press(screen.getByTestId('commit-celebration'));
    });
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it('goes away by itself after a short moment', async () => {
    jest.useFakeTimers();
    try {
      reduceMotion(false);
      const onDone = jest.fn();
      await render(<CommitCelebration message="Added 1 item" onDone={onDone} />);
      expect(screen.getByTestId('commit-celebration')).toBeTruthy();

      await act(async () => {
        jest.advanceTimersByTime(CELEBRATION_MS);
      });
      expect(onDone).toHaveBeenCalledTimes(1);
    } finally {
      jest.useRealTimers();
    }
  });

  it('renders nothing when Reduce Motion is on; the toast tells the story', async () => {
    reduceMotion(true);
    await render(<CommitCelebration message="Added 1 item" onDone={jest.fn()} />);

    expect(screen.queryByTestId('commit-celebration')).toBeNull();
  });

  it('celebrates with the dragon beside the check', async () => {
    reduceMotion(false);
    await render(<CommitCelebration message="Added 3 items" onDone={jest.fn()} />);

    expect(screen.getByTestId('celebration-dragon')).toBeTruthy();
    expect(screen.getByTestId('celebration-spot', { includeHiddenElements: true })).toBeTruthy();
  });

  it('plays the check in under a second, measured from the shipped asset', async () => {
    const playMs = ((successCheck.op - successCheck.ip) / successCheck.fr / CELEBRATION_SPEED) * 1000;
    expect(playMs).toBeLessThan(1000);
  });

  it('keeps the check without the dragon when dragons are off', async () => {
    reduceMotion(false);
    useDragonAppearance.setState({ appearance: 'off' });
    await render(<CommitCelebration message="Added 3 items" onDone={jest.fn()} />);

    expect(screen.queryByTestId('celebration-dragon')).toBeNull();
    expect(screen.getByTestId('celebration-spot', { includeHiddenElements: true })).toBeTruthy();
  });
});
