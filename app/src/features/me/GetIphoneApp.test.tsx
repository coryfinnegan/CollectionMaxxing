import { fireEvent, screen } from '@testing-library/react-native';
import { Linking } from 'react-native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { resetMockAccount } from '@/mocks/me';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { SettingsScreen } from './SettingsScreen';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

let requested: string[] = [];

beforeEach(() => {
  resetMockAccount();
  requested = [];
  server.events.on('request:start', ({ request }) => void requested.push(new URL(request.url).pathname));
  jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
});
afterEach(() => {
  server.events.removeAllListeners();
  jest.restoreAllMocks();
  resetAuth();
});

describe('Settings > Get the iPhone app', () => {
  it('explains on the web that the iPhone app comes through TestFlight, by invite', async () => {
    configureAuth({ platform: 'web' });

    await renderWithProviders(<SettingsScreen />);

    expect(await screen.findByText('Get the iPhone app')).toBeTruthy();
    expect(screen.getByText(/comes through TestFlight/)).toBeTruthy();
    expect(screen.getByText(/invite arrives by email/)).toBeTruthy();
    expect(screen.getByText(/automatic updates/)).toBeTruthy();
  });

  it('opens TestFlight on the App Store from the button', async () => {
    configureAuth({ platform: 'web' });

    await renderWithProviders(<SettingsScreen />);
    await fireEvent.press(await screen.findByLabelText('Get TestFlight'));

    expect(Linking.openURL).toHaveBeenCalledWith('https://apps.apple.com/app/testflight/id899247664');
  });

  it('no longer offers a download from the server', async () => {
    configureAuth({ platform: 'web' });

    await renderWithProviders(<SettingsScreen />);
    await screen.findByText('Get the iPhone app');

    expect(screen.queryByLabelText('Download the iPhone app')).toBeNull();
    expect(requested.filter((p) => p.startsWith('/api/v1/app/'))).toEqual([]);
  });

  it("isn't shown in the iPhone app", async () => {
    configureAuth({ platform: 'native' });

    await renderWithProviders(<SettingsScreen />);

    expect(screen.queryByText('Get the iPhone app')).toBeNull();
    expect(screen.queryByLabelText('Get TestFlight')).toBeNull();
  });
});
