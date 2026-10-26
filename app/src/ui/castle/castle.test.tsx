// The five castle primitives render in all three modes: Full shows the textures,
// Flat paints the same palette solid, Accessible drops textures and motion.
import { fireEvent, render, screen, within } from '@testing-library/react-native';
import { AccessibilityInfo, Platform, StyleSheet } from 'react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { setAppearanceStorage, useAppearanceChoice } from '@/theme/appearance';
import { castle } from '@/theme/tokens';
import { setMascotStorage, useDragonAppearance } from '../mascot/appearances';
import { BannerMarker } from './BannerMarker';
import { CastleHeader } from './CastleHeader';
import { LedgeRow } from './LedgeRow';
import { type CastleMode } from './mode';
import { setCastleStorage, useCastleThemeSetting } from './mode';
import { ParchmentCard } from './ParchmentCard';
import { TabletHeader } from './TabletHeader';
import { castleTextures, nightTextures } from './textures';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = useReduceMotion as jest.Mock;

const modes: CastleMode[] = ['full', 'flat', 'accessible'];

// Texture art hides from screen readers, so the count includes hidden elements.
function textureCount(): number {
  return screen.queryAllByTestId('castle-texture', { includeHiddenElements: true }).length;
}

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('ParchmentCard', () => {
  it.each(modes)('shows its title and body in %s mode', async (mode) => {
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." mode={mode} />);
    expect(screen.getByText('Supplies')).toBeTruthy();
    expect(screen.getByText('Rope, torches, chalk.')).toBeTruthy();
  });

  it('shows its textures in full mode', async () => {
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." mode="full" />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('shows no textures in flat mode', async () => {
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." mode="flat" />);
    expect(textureCount()).toBe(0);
  });
});

describe('TabletHeader', () => {
  it.each(modes)('shows its title in %s mode', async (mode) => {
    await render(<TabletHeader title="Armory" subtitle="Swords and shields" mode={mode} />);
    expect(screen.getByRole('header', { name: 'Armory' })).toBeTruthy();
    expect(screen.getByText('Swords and shields')).toBeTruthy();
  });

  it('shows its textures in full mode', async () => {
    await render(<TabletHeader title="Armory" mode="full" />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('shows no textures in accessible mode', async () => {
    await render(<TabletHeader title="Armory" mode="accessible" />);
    expect(textureCount()).toBe(0);
  });
});

describe('CastleHeader', () => {
  it.each(modes)('shows its title in %s mode', async (mode) => {
    await render(<CastleHeader title="Gatehouse" subtitle="Who goes there" mode={mode} />);
    expect(screen.getByRole('header', { name: 'Gatehouse' })).toBeTruthy();
    expect(screen.getByText('Who goes there')).toBeTruthy();
  });

  it('shows its textures in full mode', async () => {
    await render(<CastleHeader title="Gatehouse" mode="full" />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('shows no textures in flat mode', async () => {
    await render(<CastleHeader title="Gatehouse" mode="flat" />);
    expect(textureCount()).toBe(0);
  });

  it('perches the dragon when asked', async () => {
    await render(<CastleHeader title="Gatehouse" perch mode="full" />);
    expect(screen.getByTestId('castle-perch')).toBeTruthy();
  });

  it('leaves the perch empty without the prop', async () => {
    await render(<CastleHeader title="Gatehouse" mode="full" />);
    expect(screen.queryByTestId('castle-perch')).toBeNull();
  });

  it('leaves the perch empty when dragons are off', async () => {
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'off' });
    await render(<CastleHeader title="Gatehouse" perch mode="full" />);
    expect(screen.queryByTestId('castle-perch')).toBeNull();
    useDragonAppearance.setState({ appearance: 'full' });
  });
});

describe('BannerMarker', () => {
  it.each(modes)('shows its label in %s mode', async (mode) => {
    await render(<BannerMarker label="New" mode={mode} />);
    expect(screen.getByText('New')).toBeTruthy();
  });

  it('keeps the label beside the cloth, never on it', async () => {
    await render(<BannerMarker label="New" medium="book" mode="full" />);
    expect(screen.getByText('New')).toBeTruthy();
    expect(within(screen.getByTestId('banner-cloth')).queryByText('New')).toBeNull();
  });

  it('reads as one section header', async () => {
    await render(<BannerMarker label="New" testID="marker" mode="full" />);
    expect(screen.getByTestId('marker').props.accessibilityRole).toBe('header');
  });

  it.each([
    ['book', 'bannerBook'],
    ['game', 'bannerGame'],
    ['film', 'bannerFilm'],
  ] as const)('flies the %s cloth for the %s medium', async (medium, token) => {
    await render(<BannerMarker label="New" medium={medium} mode="flat" />);
    expect(StyleSheet.flatten(screen.getByTestId('banner-cloth').props.style).backgroundColor).toBe(castle[token]);
  });

  it('flies plain iron without a medium', async () => {
    await render(<BannerMarker label="New" mode="flat" />);
    expect(StyleSheet.flatten(screen.getByTestId('banner-cloth').props.style).backgroundColor).toBe(castle.iron);
  });

  it('hangs the cloth from a brass rod in full mode', async () => {
    await render(<BannerMarker label="New" mode="full" />);
    expect(screen.getByTestId('banner-rod')).toBeTruthy();
  });

  it.each(['flat', 'accessible'] as const)('leaves the rod off in %s mode', async (mode) => {
    await render(<BannerMarker label="New" mode={mode} />);
    expect(screen.queryByTestId('banner-rod')).toBeNull();
  });

  it('doubles the cloth edge in accessible mode', async () => {
    await render(<BannerMarker label="New" mode="accessible" />);
    expect(StyleSheet.flatten(screen.getByTestId('banner-cloth').props.style).borderWidth).toBe(2);
  });
});

describe('LedgeRow', () => {
  it.each(modes)('shows its title in %s mode', async (mode) => {
    await render(<LedgeRow title="Great hall" subtitle="Dinner at six" mode={mode} />);
    expect(screen.getByText('Great hall')).toBeTruthy();
    expect(screen.getByText('Dinner at six')).toBeTruthy();
  });

  it('shows its textures in full mode', async () => {
    await render(<LedgeRow title="Great hall" mode="full" />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('shows no textures in flat mode', async () => {
    await render(<LedgeRow title="Great hall" mode="flat" />);
    expect(textureCount()).toBe(0);
  });

  it('presses like a row', async () => {
    const onPress = jest.fn();
    await render(<LedgeRow title="Great hall" subtitle="Dinner at six" onPress={onPress} mode="flat" />);
    await fireEvent.press(screen.getByRole('button', { name: 'Great hall, Dinner at six' }));
    expect(onPress).toHaveBeenCalledTimes(1);
  });
});

describe('torchlit night art', () => {
  beforeEach(() => {
    const map = new Map<string, string>();
    setAppearanceStorage({ getString: (key: string) => map.get(key), set: (key: string, value: string) => void map.set(key, value) });
    useAppearanceChoice.setState({ choice: 'system' });
  });

  function textureSources(): unknown[] {
    return screen.queryAllByTestId('castle-texture', { includeHiddenElements: true }).map((node) => node.props.source);
  }

  it('shows the day battlement while the scheme is light', async () => {
    useAppearanceChoice.setState({ choice: 'light' });
    await render(<CastleHeader title="Gatehouse" mode="full" />);
    expect(textureSources()).toContain(castleTextures.crenel);
    expect(textureSources()).not.toContain(nightTextures.crenel);
  });

  it('shows the night battlement when the scheme is dark', async () => {
    useAppearanceChoice.setState({ choice: 'dark' });
    await render(<CastleHeader title="Gatehouse" mode="full" />);
    expect(textureSources()).toContain(nightTextures.crenel);
    expect(textureSources()).not.toContain(castleTextures.crenel);
  });

  it('frames stone tablets in night stone when the scheme is dark', async () => {
    useAppearanceChoice.setState({ choice: 'dark' });
    await render(<TabletHeader title="Armory" mode="full" />);
    expect(textureSources()).toContain(nightTextures.stone.fill);
    expect(textureSources()).not.toContain(castleTextures.stone.fill);
  });

  it('keeps parchment lit at night instead of inverting it', async () => {
    useAppearanceChoice.setState({ choice: 'dark' });
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." mode="full" />);
    expect(textureSources()).toContain(castleTextures.parchment.fill);
  });
});

describe('default mode', () => {
  beforeEach(() => {
    const map = new Map<string, string>();
    setCastleStorage({ getString: (key: string) => map.get(key), set: (key: string, value: string) => void map.set(key, value) });
    useCastleThemeSetting.setState({ setting: 'full' });
    reduceMotion.mockReset().mockReturnValue(false);
    jest.replaceProperty(Platform, 'OS', 'ios');
    jest.spyOn(AccessibilityInfo, 'isDarkerSystemColorsEnabled').mockResolvedValue(false);
    jest.spyOn(AccessibilityInfo, 'addEventListener').mockReturnValue({ remove: jest.fn() } as never);
  });

  it('renders full when the setting is full and the system is quiet', async () => {
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('renders accessible when reduce motion is on, even on full', async () => {
    reduceMotion.mockReturnValue(true);
    await render(<ParchmentCard title="Supplies" body="Rope, torches, chalk." />);
    expect(textureCount()).toBe(0);
  });
});
