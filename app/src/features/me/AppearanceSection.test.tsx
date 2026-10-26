import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { renderWithProviders } from '@/test/render';
import { applyAppearance, setAppearanceStorage, useAppearanceChoice } from '@/theme/appearance';
import { setCastleStorage, useCastleThemeSetting } from '@/ui/castle/mode';
import { setMascotStorage, useDragonAppearance } from '@/ui/mascot/appearances';
import { FakeAppIconEngine } from '../../../modules/cm-app-icon/src/FakeAppIconEngine';
import { GAMEBOY_ICON_NAME, useAppIconSetting } from './appIcon';
import { AppearanceSection } from './AppearanceSection';
import { setAppIcon, setSettingsStorage } from './services';

jest.mock('@/theme/appearance', () => ({ ...jest.requireActual('@/theme/appearance'), applyAppearance: jest.fn() }));
const applied = applyAppearance as jest.Mock;

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

function setup(initial: string | null = null, supported = true) {
  const engine = new FakeAppIconEngine(initial, supported);
  setSettingsStorage(new MemoryKeyValueStore());
  setAppIcon(engine);
  useAppIconSetting.setState({ choice: 'default', supported: false, ready: false, problem: null });
  setCastleStorage(memoryStore());
  useCastleThemeSetting.setState({ setting: 'full' });
  setMascotStorage(memoryStore());
  useDragonAppearance.setState({ appearance: 'full' });
  setAppearanceStorage(memoryStore());
  useAppearanceChoice.setState({ choice: 'system' });
  return engine;
}

beforeEach(() => {
  configureAuth({ platform: 'native' });
  applied.mockClear();
});
afterEach(() => {
  resetAuth();
  setSettingsStorage(undefined);
});

describe('Settings > Appearance', () => {
  it('offers the Default and Game Boy icons with the current one selected', async () => {
    setup(GAMEBOY_ICON_NAME);

    await renderWithProviders(<AppearanceSection />);

    expect(await screen.findByText('Appearance')).toBeTruthy();
    expect(screen.getByText('App icon')).toBeTruthy();
    expect(screen.getByLabelText('Default')).toBeTruthy();
    expect(screen.getByLabelText('Game Boy')).toBeTruthy();
    expect(screen.getByLabelText('Game Boy').props.accessibilityState.selected).toBe(true);
    expect(screen.getByLabelText('Default').props.accessibilityState.selected).toBe(false);
  });

  it('switches the icon live when a choice is tapped', async () => {
    const engine = setup(null);
    await renderWithProviders(<AppearanceSection />);
    await screen.findByText('App icon');

    await fireEvent.press(screen.getByLabelText('Game Boy'));

    expect(engine.applied).toEqual([GAMEBOY_ICON_NAME]);
    expect(screen.getByLabelText('Game Boy').props.accessibilityState.selected).toBe(true);

    await fireEvent.press(screen.getByLabelText('Default'));

    expect(engine.applied).toEqual([GAMEBOY_ICON_NAME, null]);
  });

  it('says when the switch fails and keeps the old choice selected', async () => {
    const engine = setup(null);
    engine.setIconName = async () => {
      throw new Error('the iPhone declined');
    };
    await renderWithProviders(<AppearanceSection />);
    await screen.findByText('App icon');

    await fireEvent.press(screen.getByLabelText('Game Boy'));

    expect(await screen.findByText("Couldn't switch the icon. Try again.")).toBeTruthy();
    expect(screen.getByLabelText('Default').props.accessibilityState.selected).toBe(true);
  });

  it('hides the icon row where the iPhone offers no alternates, keeping the rest', async () => {
    setup(null, false);

    await renderWithProviders(<AppearanceSection />);

    await waitFor(() => expect(useAppIconSetting.getState().ready).toBe(true));
    expect(screen.getByText('Appearance')).toBeTruthy();
    expect(screen.queryByText('App icon')).toBeNull();
    expect(screen.getByText('Castle theme')).toBeTruthy();
  });

  it('switches the castle theme between Full and Quiet', async () => {
    setup(null, false);
    await renderWithProviders(<AppearanceSection />);
    await screen.findByText('Castle theme');

    expect(screen.getByLabelText('Theme Full').props.accessibilityState.selected).toBe(true);

    await fireEvent.press(screen.getByLabelText('Theme Quiet'));

    expect(useCastleThemeSetting.getState().setting).toBe('quiet');
    expect(screen.getByLabelText('Theme Quiet').props.accessibilityState.selected).toBe(true);
  });

  it('switches the dragon between Full, Minimal and Off', async () => {
    setup(null, false);
    await renderWithProviders(<AppearanceSection />);
    await screen.findByText('Dragon appearances');

    await fireEvent.press(screen.getByLabelText('Dragon Minimal'));
    expect(useDragonAppearance.getState().appearance).toBe('minimal');

    await fireEvent.press(screen.getByLabelText('Dragon Off'));
    expect(useDragonAppearance.getState().appearance).toBe('off');
    expect(screen.getByLabelText('Dragon Off').props.accessibilityState.selected).toBe(true);
  });

  it('switches light, dark and system, applying each to the styled layer', async () => {
    setup(null, false);
    await renderWithProviders(<AppearanceSection />);
    await screen.findByText('Light or dark');

    await fireEvent.press(screen.getByLabelText('Appearance Dark'));
    expect(useAppearanceChoice.getState().choice).toBe('dark');
    await waitFor(() => expect(applied).toHaveBeenCalledWith('dark'));

    await fireEvent.press(screen.getByLabelText('Appearance System'));
    expect(useAppearanceChoice.getState().choice).toBe('system');
    await waitFor(() => expect(applied).toHaveBeenCalledWith('system'));
  });
});
