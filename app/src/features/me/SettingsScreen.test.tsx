import { QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { clearOwnedData, getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { createOwnedIndex } from '@/features/offline/ownedIndex';
import { entry } from '@/features/offline/testData';
import { setTrayStore } from '@/features/scan/tray/storage';
import { MemoryKeyValueStore, createTrayStore } from '@/features/scan/tray/trayStore';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { setNavigator } from '@/features/auth/services';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { MOCK_LIBRARY_ID, mockAccount } from '@/mocks/data';
import { resetMockAccount } from '@/mocks/me';
import { makeQueryClient, renderWithProviders } from '@/test/render';
import { useLockSetting } from './appLock';
import { registerLocalDataClearer } from './localData';
import { RecognitionConfigStore } from '@/features/scan/recognition/config';
import { setRecognition } from '@/features/scan/recognition/device';
import { EngineSelector } from '@/features/scan/recognition/EngineSelector';
import { RecordingVision } from '@/features/scan/recognition/fakes';
import { type Biometrics, setAppIcon, setBiometrics, setSettingsStorage } from './services';
import { FakeAppIconEngine } from '../../../modules/cm-app-icon/src/FakeAppIconEngine';
import { useAppIconSetting } from './appIcon';
import { SettingsScreen } from './SettingsScreen';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter, Link: ({ children }: { children: unknown }) => children }));

/** The real selector over a fake vision engine, so Settings reads what the scanner would use. */
function recognitionWith(capabilities: object) {
  const vision = new RecordingVision({}, { capabilities: capabilities as never });
  const store = new RecognitionConfigStore({
    storage: new MemoryKeyValueStore(),
    fetcher: async () => {
      throw new Error('offline');
    },
  });
  setRecognition({ store, selector: new EngineSelector({ vision, settings: () => store.current() }) });
}
const ready = { available: true, vision: true, guidedGeneration: true, contextSize: 4096 };
const unavailable = (reason: string) => ({ available: false, vision: false, guidedGeneration: false, contextSize: null, reason });

const bio: Biometrics = { isAvailable: async () => true, authenticate: async () => true };
let nativeAuth: MemoryNativeAuth;

beforeEach(() => {
  resetMockAccount();
  mockRouter.replace.mockClear();
  nativeAuth = new MemoryNativeAuth();
  configureAuth({ platform: 'native', native: nativeAuth });
  setBiometrics(bio);
  setSettingsStorage(new MemoryKeyValueStore());
  setAppIcon(new FakeAppIconEngine());
  useAppIconSetting.setState({ choice: 'default', supported: false, ready: false, problem: null });
  useLockSetting.getState().reload();
  recognitionWith(ready);
});
afterEach(() => {
  resetAuth();
  setTrayStore(undefined);
  setSettingsStorage(undefined);
  setRecognition(undefined);
});

describe('Settings > Recognition', () => {
  it('shows the Apple model when it is available', async () => {
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Apple on-device model')).toBeTruthy();
    expect(screen.getByText(/Apple Intelligence is on/)).toBeTruthy();
    expect(screen.getByText('Apple Intelligence: On')).toBeTruthy();
  });

  it('shows the Vision fallback and explains it in one line', async () => {
    recognitionWith(unavailable('appleIntelligenceOff'));
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Vision text fallback')).toBeTruthy();
    expect(screen.getByText(/Apple Intelligence isn't available/)).toBeTruthy();
    expect(screen.getByText('Apple Intelligence: Off')).toBeTruthy();
  });

  it('shows the Apple text model on a phone whose model cannot see pictures', async () => {
    recognitionWith({ available: true, vision: false, guidedGeneration: true, contextSize: 4096 });
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Apple text model')).toBeTruthy();
    expect(screen.getByText('Apple Intelligence: On')).toBeTruthy();
    expect(screen.getByText(/reads the text on covers/)).toBeTruthy();
  });

  it('never claims that covers stay on the phone, which a close call does not honor (Apple model)', async () => {
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Apple on-device model')).toBeTruthy();
    expect(screen.queryByText(/never leave/i)).toBeNull();
    expect(screen.getByText(/Covers are read on this iPhone, and nothing goes to an AI service/)).toBeTruthy();
  });

  it('never claims that cover photos stay on the phone (Apple text model)', async () => {
    recognitionWith({ available: true, vision: false, guidedGeneration: true, contextSize: 4096 });
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Apple text model')).toBeTruthy();
    expect(screen.queryByText(/never leave/i)).toBeNull();
    expect(screen.getByText(/Nothing goes to an AI service/)).toBeTruthy();
  });

  it.each([
    ['modelNotReady', 'Apple Intelligence: Still getting ready'],
    ['osTooOld', 'Apple Intelligence: Needs a newer iOS'],
    ['notSupported', 'Apple Intelligence: Not on this iPhone'],
  ])('names why the model is unavailable (%s)', async (reason, label) => {
    recognitionWith(unavailable(reason));
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Engine: Vision text fallback')).toBeTruthy();
    expect(screen.getByText(label)).toBeTruthy();
  });

  it('is hidden on the web, which offers Sign out and Sign-in methods', async () => {
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);
    expect(screen.queryByText('Recognition')).toBeNull();
    expect(screen.queryByLabelText('Unpair this iPhone')).toBeNull();
    expect(screen.getByLabelText('Sign out')).toBeTruthy();
    expect(screen.getByText('Sign-in methods')).toBeTruthy();
  });
});

describe('Settings > Appearance', () => {
  it('shows the icon switcher on the iPhone and switches live', async () => {
    const engine = new FakeAppIconEngine();
    setAppIcon(engine);
    await renderWithProviders(<SettingsScreen />);

    expect(await screen.findByText('App icon')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Game Boy'));
    expect(engine.applied).toEqual(['gameboy']);
  });

  it('shows Appearance without the icon row on the web, which has no home screen icon', async () => {
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('Appearance')).toBeTruthy();
    expect(screen.queryByText('App icon')).toBeNull();
    expect(screen.queryByText('Castle theme')).toBeNull();
    expect(screen.queryByText('Library design')).toBeNull();
    expect(screen.getByText('Light or dark')).toBeTruthy();
  });
});

describe('Settings > Libraries (iPhone)', () => {
  const SECOND = 'f0000000-0000-4000-8000-000000000002';

  it('offers New library and Join a library, with no switcher for one library', async () => {
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByText('New library')).toBeTruthy();
    expect(screen.getByText('Join a library')).toBeTruthy();
    expect(screen.queryByLabelText(/, (Owner|Viewer|Editor)/)).toBeNull();
  });

  it('switches library when there are several, and non-owners can leave', async () => {
    mockAccount.libraries = [
      { id: MOCK_LIBRARY_ID, name: 'Mine', role: 'owner' },
      { id: SECOND, name: 'Shared', role: 'viewer' },
    ];
    await renderWithProviders(<SettingsScreen />);
    expect(screen.queryByLabelText('Leave library')).toBeNull();

    await fireEvent.press(await screen.findByLabelText(/^Shared,/));

    expect(await screen.findByLabelText('Leave library')).toBeTruthy();
  });
});

describe('Settings > Sign out', () => {
  // Sign-out navigates to the URL the server returned (Auth0's sign-out; the mock sends the browser back to /login).
  const navigate = jest.fn();
  beforeEach(() => {
    navigate.mockClear();
    setNavigator(navigate);
  });

  it('runs every registered local-data clearer and empties the query cache (web)', async () => {
    const clearer = jest.fn();
    const unregister = registerLocalDataClearer(clearer);
    configureAuth({ platform: 'web' });
    const queryClient = makeQueryClient();
    await render(
      <QueryClientProvider client={queryClient}>
        <SettingsScreen />
      </QueryClientProvider>,
    );
    queryClient.setQueryData(['anything'], { cached: true });

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/login'));
    expect(clearer).toHaveBeenCalledTimes(1);
    expect(queryClient.getQueryData(['anything'])).toBeUndefined();
    unregister();
  });

  it('clears the result cards', async () => {
    useCardsStore.getState().reset();
    useCardsStore.getState().merge([{ id: 'card-1', localId: 'local-dune' }], { announce: false });
    expect(Object.keys(useCardsStore.getState().byId)).toHaveLength(1);
    configureAuth({ platform: 'web' });
    const queryClient = makeQueryClient();
    await render(
      <QueryClientProvider client={queryClient}>
        <SettingsScreen />
      </QueryClientProvider>,
    );

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/login'));
    expect(useCardsStore.getState().byId).toEqual({});
  });

  it('empties the offline owned copy', async () => {
    const storage = new MemoryOwnedStorage();
    setOwnedStorage(storage);
    getOwnedCache('lib-1').commit({ index: createOwnedIndex([entry({ title: 'Jaws' })]), cursor: 'c1', syncedAt: 1 });
    const unregister = registerLocalDataClearer(clearOwnedData); // the app registers this through OwnedDataBridge
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);

    await fireEvent.press(screen.getByLabelText('Sign out'));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/login'));
    unregister();
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

  it('signs out: revokes at Auth0, wipes the credentials, offline index and tray, and returns to sign-in', async () => {
    const tray = createTrayStore({ storage: new MemoryKeyValueStore() });
    tray.getState().scan({ value: '0123456789012', symbology: 'ean-13' });
    expect(tray.getState().lines.length).toBe(1);
    setTrayStore(tray);
    const offline = jest.fn();
    const unregister = registerLocalDataClearer(offline);
    await renderWithProviders(<SettingsScreen />);

    await fireEvent.press(screen.getByLabelText('Sign out of this iPhone'));
    expect(nativeAuth.signedOut).toBe(false); // asks first
    await fireEvent.press(screen.getByLabelText('Sign out and remove local data'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(nativeAuth.signedOut).toBe(true);
    expect(nativeAuth.token).toBeNull();
    expect(offline).toHaveBeenCalled();
    expect(tray.getState().lines).toHaveLength(0);
    unregister();
  });
});
