import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { createOwnedIndex } from '@/features/offline/ownedIndex';
import { entry } from '@/features/offline/testData';
import { setTrayStore } from '@/features/scan/tray/storage';
import { MemoryKeyValueStore, createTrayStore } from '@/features/scan/tray/trayStore';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryTokenStore } from '@/lib/auth/tokenStore';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { renderWithProviders } from '@/test/render';
import { useLockSetting } from './appLock';
import { registerLocalDataClearer } from './localData';
import { type Biometrics, setBiometrics, setSettingsStorage, setVision } from './services';
import { SettingsScreen } from './SettingsScreen';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const bio: Biometrics = { isAvailable: async () => true, authenticate: async () => true };
let tokenStore: MemoryTokenStore;

beforeEach(() => {
  resetMockAccount();
  mockRouter.replace.mockClear();
  tokenStore = new MemoryTokenStore({ refreshToken: 'mock-refresh', serverUrl: 'http://api.test' });
  configureAuth({ platform: 'native', tokenStore });
  setBiometrics(bio);
  setSettingsStorage(new MemoryKeyValueStore());
  useLockSetting.getState().reload();
  setVision({ isAvailable: async () => true });
});
afterEach(() => {
  resetAuth();
  setTrayStore(undefined);
  setSettingsStorage(undefined);
});

describe('Settings > Recognition', () => {
  it('shows the Apple model when it is available', async () => {
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Apple on-device model')).toBeTruthy();
    expect(screen.getByText(/Apple Intelligence is on/)).toBeTruthy();
  });

  it('shows the Vision fallback and explains it in one line', async () => {
    setVision({ isAvailable: async () => false });
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Vision text fallback')).toBeTruthy();
    expect(screen.getByText(/Apple Intelligence isn't available/)).toBeTruthy();
  });

  it('is hidden on the web, which only offers Sign out', async () => {
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);
    expect(screen.queryByText('Recognition')).toBeNull();
    expect(screen.queryByLabelText('Unpair this iPhone')).toBeNull();
    expect(screen.getByLabelText('Sign out')).toBeTruthy();
  });
});

describe('Settings > Sign out', () => {
  it('empties the offline owned copy', async () => {
    const storage = new MemoryOwnedStorage();
    setOwnedStorage(storage);
    getOwnedCache('lib-1').commit({ index: createOwnedIndex([entry({ title: 'Jaws' })]), cursor: 'c1', syncedAt: 1 });
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(getOwnedCache('lib-1').state.syncedAt).toBeNull();
    expect(storage.getString('owned-index-v1:lib-1')).toBeUndefined();
    setOwnedStorage(undefined);
  });
});

describe('Settings > Security', () => {
  it('turns the Face ID lock off and on', async () => {
    await renderWithProviders(<SettingsScreen />);
    await fireEvent.press(screen.getByLabelText('Turn off Face ID lock'));
    expect(screen.getByText('Face ID lock is off.')).toBeTruthy();
    expect(useLockSetting.getState().enabled).toBe(false);
    await fireEvent.press(screen.getByLabelText('Turn on Face ID lock'));
    expect(useLockSetting.getState().enabled).toBe(true);
  });

  it('unpairs: revokes, wipes the Keychain, offline index and tray, and returns to pairing', async () => {
    const tray = createTrayStore({ storage: new MemoryKeyValueStore() });
    tray.getState().scan({ value: '0123456789012', symbology: 'ean-13' });
    expect(tray.getState().lines.length).toBe(1);
    setTrayStore(tray);
    const offline = jest.fn();
    const unregister = registerLocalDataClearer(offline);
    await renderWithProviders(<SettingsScreen />);

    await fireEvent.press(screen.getByLabelText('Unpair this iPhone'));
    expect(mockAccount.revoked).toHaveLength(0); // asks first
    await fireEvent.press(screen.getByLabelText('Unpair and remove local data'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/pair'));
    expect(mockAccount.revoked).toEqual(['mock-refresh']);
    expect(await tokenStore.load()).toBeNull();
    expect(offline).toHaveBeenCalled();
    expect(tray.getState().lines).toHaveLength(0);
    unregister();
  });
});
