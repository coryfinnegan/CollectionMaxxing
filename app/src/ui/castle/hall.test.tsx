// The Great Hall pieces (#363): shield markers reusing the mascot's shield art, a
// hoard pile that never visually shrinks, and the themed state frame.
import { render, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { castle } from '@/theme/tokens';
import { Text } from '../Text';
import { setMascotStorage, useDragonAppearance } from '../mascot/appearances';
import { CastleState } from './CastleState';
import { hoardPeak, setHoardStorage } from './hoard';
import { coinsForPeak, HoardMeter } from './HoardMeter';
import { SeriesShield } from './SeriesShield';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = jest.mocked(useReduceMotion);

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('SeriesShield', () => {
  it('shows the shared shield beside its label, never under it', async () => {
    await render(<SeriesShield label="100 items" sublabel="Shelved them all" />);
    expect(screen.getByTestId('mascot-layer-shield', { includeHiddenElements: true })).toBeTruthy();
    expect(screen.getByText('100 items')).toBeTruthy();
    expect(screen.getByText('Shelved them all')).toBeTruthy();
  });
});

describe('coinsForPeak', () => {
  it.each([
    [0, 0],
    [1, 2],
    [10, 8],
    [50, 14],
    [100, 16],
    [250, 19],
    [500, 22],
    [999, 24],
    [1000, 24],
    [5000, 24],
  ])('piles %s coins for a peak of %s', (peak, coins) => {
    expect(coinsForPeak(peak)).toBe(coins);
  });

  it('never shrinks as the peak grows', () => {
    let coins = 0;
    for (let peak = 0; peak <= 2000; peak += 7) {
      const next = coinsForPeak(peak);
      expect(next).toBeGreaterThanOrEqual(coins);
      coins = next;
    }
  });
});

describe('hoardPeak', () => {
  beforeEach(() => {
    setHoardStorage(memoryStore());
  });

  it('rises with the count and never falls when items leave', () => {
    expect(hoardPeak('lib', 35)).toBe(35);
    expect(hoardPeak('lib', 12)).toBe(35);
    expect(hoardPeak('lib', 40)).toBe(40);
  });

  it('keeps a peak per library', () => {
    expect(hoardPeak('one', 35)).toBe(35);
    expect(hoardPeak('two', 3)).toBe(3);
    expect(hoardPeak('one', 1)).toBe(35);
  });
});

describe('HoardMeter', () => {
  it('piles coins for the peak while the words report the live count', async () => {
    await render(<HoardMeter peak={100} total={12} />);
    expect(screen.getByTestId('hoard-coins').props.children).toHaveLength(coinsForPeak(100));
    expect(screen.getByText('12 items on your shelves')).toBeTruthy();
  });

  it('coins the pile in brass', async () => {
    await render(<HoardMeter peak={10} total={10} />);
    const first = screen.getByTestId('hoard-coins').props.children[0].props.style;
    expect(StyleSheet.flatten(first).backgroundColor).toBe(castle.brass);
  });
});

describe('CastleState', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('frames the words in parchment under the state pose', async () => {
    await render(
      <CastleState pose="emptyHoard" mode="full" testID="state">
        <Text>Nothing here yet.</Text>
      </CastleState>,
    );
    expect(screen.getByTestId('castle-state-dragon')).toBeTruthy();
    expect(screen.getByText('Nothing here yet.')).toBeTruthy();
    expect(screen.queryAllByTestId('castle-texture', { includeHiddenElements: true }).length).toBeGreaterThan(0);
  });

  it('keeps the words when dragons are off', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    await render(
      <CastleState pose="emptyHoard" mode="full" testID="state">
        <Text>Nothing here yet.</Text>
      </CastleState>,
    );
    expect(screen.queryByTestId('castle-state-dragon')).toBeNull();
    expect(screen.getByText('Nothing here yet.')).toBeTruthy();
    useDragonAppearance.setState({ appearance: 'full' });
  });
});
