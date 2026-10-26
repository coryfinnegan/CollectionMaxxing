// The seasonal banners (#362): the cloth follows the season outside, and Dragon
// Off takes the banner down entirely.
import { render, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { castle } from '@/theme/tokens';
import { setMascotStorage, useDragonAppearance } from '../mascot/appearances';
import { SeasonalBanner } from './SeasonalBanner';
import { seasonCloth, seasonFor, type Season } from './season';

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

describe('seasonFor', () => {
  it.each([
    ['January', 0, 'winter'],
    ['February', 1, 'winter'],
    ['March', 2, 'spring'],
    ['April', 3, 'spring'],
    ['May', 4, 'spring'],
    ['June', 5, 'summer'],
    ['July', 6, 'summer'],
    ['August', 7, 'summer'],
    ['September', 8, 'autumn'],
    ['October', 9, 'autumn'],
    ['November', 10, 'autumn'],
    ['December', 11, 'winter'],
  ])('hangs the %s cloth in %s', (_name, month, season) => {
    expect(seasonFor(new Date(2026, month as number, 15))).toBe(season as Season);
  });
});

describe('seasonCloth', () => {
  it('dyes every season from the castle graphics tokens', () => {
    for (const key of Object.values(seasonCloth)) {
      expect(castle[key]).toMatch(/^#[0-9a-f]{6}$/);
    }
  });
});

describe('SeasonalBanner', () => {
  it('hangs the given season in its cloth color beside its name', async () => {
    await render(<SeasonalBanner season="winter" mode="full" testID="season" />);

    expect(screen.getByText('Winter')).toBeTruthy();
    const cloth = screen.getByTestId('banner-cloth');
    expect(StyleSheet.flatten(cloth.props.style).backgroundColor).toBe(castle[seasonCloth.winter]);
  });

  it('defaults to the season outside the window', async () => {
    await render(<SeasonalBanner mode="full" testID="season" />);

    const names = { spring: 'Spring', summer: 'Summer', autumn: 'Autumn', winter: 'Winter' } as const;
    expect(screen.getByText(names[seasonFor(new Date())])).toBeTruthy();
  });

  it('renders nothing when dragons are off', async () => {
    useDragonAppearance.setState({ appearance: 'off' });
    await render(<SeasonalBanner season="winter" mode="full" testID="season" />);

    expect(screen.queryByTestId('season')).toBeNull();
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('stays up on minimal; the board is content, not decoration', async () => {
    useDragonAppearance.setState({ appearance: 'minimal' });
    await render(<SeasonalBanner season="winter" mode="full" testID="season" />);

    expect(screen.getByTestId('season')).toBeTruthy();
    useDragonAppearance.setState({ appearance: 'full' });
  });
});
