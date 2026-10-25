import { fireEvent, render, screen } from '@testing-library/react-native';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { Button } from './Button';
import { Chip } from './Chip';
import { ListRow } from './ListRow';

const fake = () => {
  const calls: string[] = [];
  const driver: HapticDriver = {
    tap: () => void calls.push('tap'),
    mediumTap: () => void calls.push('medium'),
    longTap: () => void calls.push('long'),
    tick: () => void calls.push('tick'),
    success: () => void calls.push('success'),
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  return calls;
};

afterEach(() => setHapticDriver(null));

describe('primitive press feedback', () => {
  it('taps lightly when a button is pressed', async () => {
    const calls = fake();
    const onPress = jest.fn();
    await render(<Button label="Save" onPress={onPress} />);
    await fireEvent.press(screen.getByRole('button', { name: 'Save' }));
    expect(onPress).toHaveBeenCalledTimes(1);
    expect(calls).toEqual(['tap']);
  });

  it('stays silent when a disabled button cannot be pressed', async () => {
    const calls = fake();
    await render(<Button label="Save" disabled onPress={jest.fn()} />);
    await fireEvent.press(screen.getByRole('button', { name: 'Save' }));
    expect(calls).toEqual([]);
  });

  it('commits with weight when a button says so', async () => {
    const calls = fake();
    await render(<Button label="Save changes" haptic="commit" onPress={jest.fn()} />);
    await fireEvent.press(screen.getByRole('button', { name: 'Save changes' }));
    expect(calls).toEqual(['medium']);
  });

  it('ticks when a chip is picked', async () => {
    const calls = fake();
    await render(<Chip label="Games" onPress={jest.fn()} />);
    await fireEvent.press(screen.getByRole('button', { name: 'Games' }));
    expect(calls).toEqual(['tick']);
  });

  it('taps lightly when a list row is opened', async () => {
    const calls = fake();
    await render(<ListRow title="Shelf 1" onPress={jest.fn()} />);
    await fireEvent.press(screen.getByRole('button', { name: 'Shelf 1' }));
    expect(calls).toEqual(['tap']);
  });
});
