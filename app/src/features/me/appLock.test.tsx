import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Text } from 'react-native';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { AppLockGate, LOCK_GRACE_MS, useLockSetting } from './appLock';
import { type Biometrics, setBiometrics, setSettingsStorage } from './services';

class FakeBiometrics implements Biometrics {
  available = true;
  succeed = true;
  prompts = 0;
  async isAvailable() {
    return this.available;
  }
  async authenticate() {
    this.prompts++;
    return this.succeed;
  }
}

const fake = new FakeBiometrics();
let listener: (state: never) => void = () => {};
let clock = 0;
const appState = {
  addEventListener: (_type: 'change', l: (state: never) => void) => {
    listener = l;
    return { remove: () => {} };
  },
};
const send = (state: string) => act(async () => listener(state as never));

const mount = async () => {
  await render(
    <AppLockGate appState={appState} now={() => clock}>
      <Text>Secret library</Text>
    </AppLockGate>,
  );
  await act(async () => {});
};

beforeEach(() => {
  clock = 0;
  Object.assign(fake, { available: true, succeed: true, prompts: 0 });
  setBiometrics(fake);
  setSettingsStorage(new MemoryKeyValueStore());
  useLockSetting.getState().reload();
  configureAuth({ platform: 'native' });
});
afterEach(() => {
  resetAuth();
  setSettingsStorage(undefined);
});

describe('AppLockGate', () => {
  it('asks for Face ID when the app opens and shows the content once it succeeds', async () => {
    await mount();
    expect(fake.prompts).toBe(1);
    expect(screen.getByText('Secret library')).toBeTruthy();
  });

  it('requires auth after backgrounding past the grace period, and a failed auth keeps content hidden', async () => {
    await mount();
    await send('background');
    clock = LOCK_GRACE_MS + 1;
    fake.succeed = false;

    await send('active');

    expect(await screen.findByText('CollectionMaxxing is locked')).toBeTruthy();
    expect(screen.queryByText('Secret library')).toBeNull();
    expect(await screen.findByText(/Couldn't unlock/)).toBeTruthy();
  });

  it('unlocks once auth succeeds', async () => {
    await mount();
    await send('background');
    clock = LOCK_GRACE_MS + 1;
    fake.succeed = false;
    await send('active');
    await screen.findByText('CollectionMaxxing is locked');

    fake.succeed = true;
    await fireEvent.press(screen.getByLabelText('Unlock'));

    await waitFor(() => expect(screen.getByText('Secret library')).toBeTruthy());
  });

  it('does not ask again for a quick trip to the background', async () => {
    await mount();
    const before = fake.prompts;
    await send('background');
    clock = LOCK_GRACE_MS - 1;
    await send('active');
    expect(fake.prompts).toBe(before);
  });

  it('uses the five-minute grace period from docs/07', () => {
    expect(LOCK_GRACE_MS).toBe(5 * 60_000);
  });

  it('does not ask again at 4:59 away', async () => {
    await mount();
    const before = fake.prompts;
    await send('background');
    clock = 4 * 60_000 + 59_000;
    await send('active');
    expect(fake.prompts).toBe(before);
  });

  it('asks again at 5:01 away', async () => {
    await mount();
    const before = fake.prompts;
    await send('background');
    clock = 5 * 60_000 + 1_000;
    fake.succeed = false;
    await send('active');
    expect(await screen.findByText('CollectionMaxxing is locked')).toBeTruthy();
    expect(fake.prompts).toBeGreaterThan(before);
  });

  it('does nothing when the lock is off, and the setting persists', async () => {
    const storage = new MemoryKeyValueStore();
    setSettingsStorage(storage);
    useLockSetting.getState().set(false);
    expect(storage.getString('face-id-lock')).toBe('off');
    useLockSetting.setState({ enabled: true }); // forget the in-memory value
    useLockSetting.getState().reload();
    expect(useLockSetting.getState().enabled).toBe(false);

    await mount();
    await send('background');
    clock = LOCK_GRACE_MS * 10;
    await send('active');

    expect(fake.prompts).toBe(0);
    expect(screen.getByText('Secret library')).toBeTruthy();
  });

  describe('snapshot cover', () => {
    const cover = () => screen.queryByTestId('snapshot-cover', { includeHiddenElements: true });

    it('covers the content the moment the app goes inactive and removes the cover on active', async () => {
      await mount();
      expect(cover()).toBeNull();

      await send('inactive');
      expect(cover()).toBeTruthy();
      expect(screen.queryByText('Secret library')).toBeNull();

      await send('active');
      expect(cover()).toBeNull();
      expect(screen.getByText('Secret library')).toBeTruthy();
    });

    it('also covers on background', async () => {
      await mount();
      await send('background');
      expect(cover()).toBeTruthy();
    });

    it('covers even when the Face ID lock is off', async () => {
      useLockSetting.getState().set(false);
      await mount();
      await send('inactive');
      expect(cover()).toBeTruthy();
      expect(screen.queryByText('Secret library')).toBeNull();
      expect(fake.prompts).toBe(0);
    });

    it('hands over to the lock screen when the lock is due', async () => {
      await mount();
      await send('background');
      clock = LOCK_GRACE_MS + 1;
      fake.succeed = false;
      await send('active');
      expect(cover()).toBeNull();
      expect(await screen.findByText('CollectionMaxxing is locked')).toBeTruthy();
    });

    it('is hidden from VoiceOver and does not animate', async () => {
      await mount();
      await send('inactive');
      const node = screen.getByTestId('snapshot-cover', { includeHiddenElements: true });
      expect(node.props.accessibilityElementsHidden).toBe(true);
      expect(node.props.importantForAccessibility).toBe('no-hide-descendants');
    });

    it('never shows on the web build', async () => {
      configureAuth({ platform: 'web' });
      await mount();
      await send('inactive');
      expect(cover()).toBeNull();
    });
  });

  it('never locks the web app', async () => {
    configureAuth({ platform: 'web' });
    await mount();
    expect(fake.prompts).toBe(0);
  });
});
