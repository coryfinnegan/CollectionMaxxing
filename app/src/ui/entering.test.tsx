// Entering and layout animations (#304): chips, rows, and sheets settle in fast, and
// stay still when Reduce Motion is on.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Chip } from './Chip';
import { ListRow } from './ListRow';
import { Sheet } from './Sheet';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

describe('entering animations', () => {
  it('settles a chip in with a layout animation', async () => {
    reduceMotion(false);
    await render(<Chip label="Games" testID="chip" onPress={jest.fn()} />);
    const wrapper = screen.getByTestId('chip').parent!;
    expect(wrapper.props.entering).toBeDefined();
    expect(wrapper.props.layout).toBeDefined();
  });

  it('keeps a chip still when Reduce Motion is on', async () => {
    reduceMotion(true);
    await render(<Chip label="Games" testID="chip" onPress={jest.fn()} />);
    const wrapper = screen.getByTestId('chip').parent!;
    expect(wrapper.props.entering).toBeUndefined();
    expect(wrapper.props.layout).toBeUndefined();
  });

  it('settles a list row in, pressed or plain', async () => {
    reduceMotion(false);
    await render(<ListRow title="Shelf 1" testID="row" onPress={jest.fn()} />);
    expect(screen.getByTestId('row').parent!.props.entering).toBeDefined();

    const plain = await render(<ListRow title="Shelf 2" testID="plain" />);
    expect(plain.getByTestId('plain').props.entering).toBeDefined();
  });

  it('keeps list rows still when Reduce Motion is on', async () => {
    reduceMotion(true);
    await render(<ListRow title="Shelf 1" testID="row" onPress={jest.fn()} />);
    const wrapper = screen.getByTestId('row').parent!;
    expect(wrapper.props.entering).toBeUndefined();
    expect(wrapper.props.layout).toBeUndefined();
  });

  it('drops a sheet in from above, or shows it at once when Reduce Motion is on', async () => {
    reduceMotion(false);
    const animated = await render(
      <Sheet title="Format" testID="sheet" onClose={jest.fn()}>
        <Chip label="DVD" />
      </Sheet>,
    );
    expect(animated.getByTestId('sheet').props.entering).toBeDefined();

    reduceMotion(true);
    const still = await render(
      <Sheet title="Format" testID="sheet" onClose={jest.fn()}>
        <Chip label="DVD" />
      </Sheet>,
    );
    expect(still.getByTestId('sheet').props.entering).toBeUndefined();
  });
});
