// A placed dragon (#363): the Dragon appearances setting decides which placements
// show. Decorative placements (the Library header perch) show on Full only; state
// placements (empty, loading, error, milestone) also show on Minimal; Off shows none.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { setMascotStorage, useDragonAppearance } from './appearances';
import { PlacedMascot } from './PlacedMascot';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = jest.mocked(useReduceMotion);

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('PlacedMascot', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('shows the decorative perch on full', async () => {
    await render(<PlacedMascot pose="idle" placement="decorative" testID="perch" />);
    expect(screen.getByTestId('perch')).toBeTruthy();
  });

  it('hides the decorative perch on minimal', async () => {
    useDragonAppearance.setState({ appearance: 'minimal' });
    await render(<PlacedMascot pose="idle" placement="decorative" testID="perch" />);
    expect(screen.queryByTestId('perch')).toBeNull();
  });

  it('keeps state poses on minimal', async () => {
    useDragonAppearance.setState({ appearance: 'minimal' });
    await render(<PlacedMascot pose="emptyHoard" placement="state" testID="empty" />);
    expect(screen.getByTestId('empty')).toBeTruthy();
  });

  it('shows nothing on off', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    await render(<PlacedMascot pose="emptyHoard" placement="state" testID="empty" />);
    await render(<PlacedMascot pose="idle" placement="decorative" testID="perch" />);
    expect(screen.queryByTestId('empty')).toBeNull();
    expect(screen.queryByTestId('perch')).toBeNull();
  });
});
