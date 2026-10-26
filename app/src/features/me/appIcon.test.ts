import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { FakeAppIconEngine } from '../../../modules/cm-app-icon/src/FakeAppIconEngine';
import type { AppIconEngine } from '../../../modules/cm-app-icon/src/types';
import { GAMEBOY_ICON_NAME, useAppIconSetting } from './appIcon';
import { setAppIcon, setSettingsStorage } from './services';

function reset(appIcon: AppIconEngine, stored?: string) {
  const storage = new MemoryKeyValueStore();
  if (stored !== undefined) storage.set('app-icon-choice', stored);
  setSettingsStorage(storage);
  setAppIcon(appIcon);
  useAppIconSetting.setState({ choice: 'default', supported: false, ready: false, problem: null });
  return storage;
}

afterEach(() => {
  setSettingsStorage(undefined);
});

describe('app icon setting', () => {
  it('reads the iPhone on refresh: the alternate when it is active', async () => {
    reset(new FakeAppIconEngine(GAMEBOY_ICON_NAME));

    await useAppIconSetting.getState().refresh();

    const state = useAppIconSetting.getState();
    expect(state).toMatchObject({ choice: 'gameboy', supported: true, ready: true, problem: null });
  });

  it('corrects a stale stored choice from the iPhone and stores the truth', async () => {
    const storage = reset(new FakeAppIconEngine(null), 'gameboy');

    await useAppIconSetting.getState().refresh();

    expect(useAppIconSetting.getState().choice).toBe('default');
    expect(storage.getString('app-icon-choice')).toBe('default');
  });

  it('reads garbage and unknown icon names as the default', async () => {
    reset(new FakeAppIconEngine('something-else'), 'teal');

    await useAppIconSetting.getState().refresh();

    expect(useAppIconSetting.getState().choice).toBe('default');
  });

  it('stays not ready and unsupported where the iPhone offers no alternates', async () => {
    const engine = new FakeAppIconEngine(null, false);
    reset(engine);

    await useAppIconSetting.getState().refresh();

    expect(useAppIconSetting.getState()).toMatchObject({ supported: false, ready: true });
  });

  it('switches live and persists the choice', async () => {
    const engine = new FakeAppIconEngine(null);
    const storage = reset(engine);
    await useAppIconSetting.getState().refresh();

    await useAppIconSetting.getState().set('gameboy');

    expect(engine.applied).toEqual([GAMEBOY_ICON_NAME]);
    expect(useAppIconSetting.getState().choice).toBe('gameboy');
    expect(storage.getString('app-icon-choice')).toBe('gameboy');

    await useAppIconSetting.getState().set('default');

    expect(engine.applied).toEqual([GAMEBOY_ICON_NAME, null]);
    expect(useAppIconSetting.getState().choice).toBe('default');
  });

  it('does not call the iPhone when the choice is unchanged or unsupported', async () => {
    const engine = new FakeAppIconEngine(GAMEBOY_ICON_NAME);
    reset(engine);
    await useAppIconSetting.getState().refresh();
    await useAppIconSetting.getState().set('gameboy');
    expect(engine.applied).toEqual([]);

    const unsupported = new FakeAppIconEngine(null, false);
    reset(unsupported);
    await useAppIconSetting.getState().refresh();
    await useAppIconSetting.getState().set('gameboy');
    expect(unsupported.applied).toEqual([]);
  });

  it('a failed switch keeps the old choice and reports a problem', async () => {
    const engine = new FakeAppIconEngine(null);
    engine.setIconName = async () => {
      throw new Error('the iPhone declined');
    };
    reset(engine);
    await useAppIconSetting.getState().refresh();

    await useAppIconSetting.getState().set('gameboy');

    const state = useAppIconSetting.getState();
    expect(state.choice).toBe('default');
    expect(state.problem).toMatch("Couldn't switch the icon");
  });
});
