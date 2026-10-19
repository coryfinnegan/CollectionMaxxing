import { fireEvent, screen } from '@testing-library/react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { renderWithProviders } from '@/test/render';
import { setSharer } from './services';
import { SettingsScreen } from './SettingsScreen';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

const deliver = jest.fn(async () => {});
const SUM = 'a'.repeat(64);

beforeEach(() => {
  resetMockAccount();
  deliver.mockClear();
  setSharer({ deliver });
});
afterEach(() => resetAuth());

describe('Settings > Get the iPhone app', () => {
  it('shows the date, size and checksum on the web and downloads the fixed route', async () => {
    configureAuth({ platform: 'web' });
    mockAccount.ipa = { sizeBytes: 5 * 1024 * 1024, modifiedAt: '2026-10-01T12:00:00Z', sha256: SUM };

    await renderWithProviders(<SettingsScreen />);

    expect(await screen.findByText(/Latest build: Oct 1, 2026, 5.0 MB/)).toBeTruthy();
    expect(screen.getByText(`SHA-256: ${SUM}`)).toBeTruthy();
    expect(screen.getByText(/AltStore/)).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Download the iPhone app'));
    expect(deliver).toHaveBeenCalledWith('/api/v1/app/ipa');
  });

  it('says it is not available yet when no build has been synced', async () => {
    configureAuth({ platform: 'web' });

    await renderWithProviders(<SettingsScreen />);

    expect(await screen.findByText('Not available yet: it appears after the next release.')).toBeTruthy();
    expect(screen.queryByLabelText('Download the iPhone app')).toBeNull();
  });

  it("isn't shown in the iPhone app", async () => {
    configureAuth({ platform: 'native' });
    mockAccount.ipa = { sizeBytes: 1024, modifiedAt: '2026-10-01T12:00:00Z', sha256: null };

    await renderWithProviders(<SettingsScreen />);

    expect(screen.queryByText('Get the iPhone app')).toBeNull();
    expect(screen.queryByLabelText('Download the iPhone app')).toBeNull();
  });
});
