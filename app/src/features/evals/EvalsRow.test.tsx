import { screen, waitFor } from '@testing-library/react-native';
import { RecognitionConfigStore } from '@/features/scan/recognition/config';
import { setRecognition } from '@/features/scan/recognition/device';
import { EngineSelector } from '@/features/scan/recognition/EngineSelector';
import { RecordingVision } from '@/features/scan/recognition/fakes';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { SettingsScreen } from '@/features/me/SettingsScreen';
import { setSettingsStorage } from '@/features/me/services';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryTokenStore } from '@/lib/auth/tokenStore';
import { evalsMock, goldenPhotos } from '@/mocks/evals';
import { renderWithProviders } from '@/test/render';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

function native() {
  configureAuth({ platform: 'native', tokenStore: new MemoryTokenStore({ refreshToken: 'mock-refresh', serverUrl: 'http://api.test' }) });
}

beforeEach(() => {
  evalsMock.reset();
  native();
  setSettingsStorage(new MemoryKeyValueStore());
  const vision = new RecordingVision({});
  const store = new RecognitionConfigStore({
    storage: new MemoryKeyValueStore(),
    fetcher: async () => {
      throw new Error('offline');
    },
  });
  setRecognition({ store, selector: new EngineSelector({ vision, settings: () => store.current() }) });
});
afterEach(() => {
  resetAuth();
  setSettingsStorage(undefined);
  setRecognition(undefined);
});

describe('Settings > Recognition > Run evals', () => {
  it('shows the row to the owner when the PC lists golden photos', async () => {
    evalsMock.access = 'enabled';
    evalsMock.photos = goldenPhotos(3);
    await renderWithProviders(<SettingsScreen />);
    expect(await screen.findByRole('button', { name: 'Run evals' })).toBeTruthy();
  });

  it.each([
    ['evals are off on the PC (404)', 'off'],
    ['the person is not the owner (403)', 'forbidden'],
  ] as const)('hides the row when %s', async (_name, access) => {
    evalsMock.access = access;
    await renderWithProviders(<SettingsScreen />);
    await screen.findByText('Recognition');
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Run evals' })).toBeNull());
    await new Promise((resolve) => setTimeout(resolve, 50)); // the probe has answered by now
    expect(screen.queryByRole('button', { name: 'Run evals' })).toBeNull();
  });

  it('hides the row on the web, and does not even ask the server', async () => {
    evalsMock.access = 'enabled';
    evalsMock.photos = goldenPhotos(3);
    configureAuth({ platform: 'web' });
    await renderWithProviders(<SettingsScreen />);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole('button', { name: 'Run evals' })).toBeNull();
  });
});
