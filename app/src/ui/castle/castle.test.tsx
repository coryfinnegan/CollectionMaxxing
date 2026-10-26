// The five castle primitives render in all three modes: Full shows the textures,
// Flat paints the same palette solid, Accessible drops textures and motion.
import { fireEvent, render, screen } from '@testing-library/react-native';
import { AccessibilityInfo, Platform } from 'react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { BannerMarker } from './BannerMarker';
import { CastleHeader } from './CastleHeader';
import { LedgeRow } from './LedgeRow';
import { type CastleMode } from './mode';
import { setCastleStorage, useCastleThemeSetting } from './mode';
import { ParchmentCard } from './ParchmentCard';
import { TabletHeader } from './TabletHeader';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = useReduceMotion as jest.Mock;

const modes: CastleMode[] = ['full', 'flat', 'accessible'];

// Texture art hides from screen readers, so the count includes hidden elements.
function textureCount(): number {
  return screen.queryAllByTestId('castle-texture', { includeHiddenElements: true }).length;
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
});

describe('BannerMarker', () => {
  it.each(modes)('shows its label in %s mode', async (mode) => {
    await render(<BannerMarker label="New" mode={mode} />);
    expect(screen.getByText('New')).toBeTruthy();
  });

  it('shows its textures in full mode', async () => {
    await render(<BannerMarker label="New" mode="full" />);
    expect(textureCount()).toBeGreaterThan(0);
  });

  it('shows no textures in accessible mode', async () => {
    await render(<BannerMarker label="New" mode="accessible" />);
    expect(textureCount()).toBe(0);
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
