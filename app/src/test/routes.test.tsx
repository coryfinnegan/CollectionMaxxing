// Render smoke test: every route in the navigation map renders its screen name.
import { renderRouter, screen } from 'expo-router/testing-library';
import * as sdk from 'react-native-auth0';
import { resetAuth } from '@/lib/auth/session';

// The Jest stand-in for react-native-auth0 (app/__mocks__): the real sign-in code runs on top of it.
const { mockAuth0, resetMockAuth0 } = sdk as unknown as { mockAuth0: Record<string, Record<string, jest.Mock>>; resetMockAuth0: () => void };

const routes: [url: string, expected: string][] = [
  ['/library', 'Library'],
  ['/scan', 'Scan'],
  ['/me', 'Me'],
  ['/me/locations', 'Locations'],
  ['/me/export', 'Export my data'],
  ['/me/devices', 'Devices and browsers'],
  ['/me/delete', 'Delete account'],
  ['/wishlist', 'Wishlist'],
  ['/lent-out', 'Lent out'],
  ['/members', 'Members'],
  ['/settings', 'Settings'],
  ['/about', 'About'],
  ['/item/42', 'Item'],
  ['/item/42/edit', 'Edit item'],
  ['/scanner', 'Scanner'],
  ['/review', 'Review'],
  ['/result/7', 'Result'],
];

describe('routes', () => {
  beforeEach(() => resetMockAuth0());
  afterEach(() => resetAuth());

  it.each(routes)('%s renders', async (url, expected) => {
    await renderRouter('./src/app', { initialUrl: url });
    expect((await screen.findAllByText(expected)).length).toBeGreaterThan(0);
  });

  it('opens on Sign in at first launch, when there is no sign-in yet', async () => {
    await renderRouter('./src/app', { initialUrl: '/' });
    expect(await screen.findByText('Sign in to your library.')).toBeTruthy();
  });

  it('opens the library when the iPhone has a working sign-in', async () => {
    mockAuth0.credentialsManager.hasValidCredentials.mockResolvedValue(true);
    await renderRouter('./src/app', { initialUrl: '/' });
    expect((await screen.findAllByText('Library')).length).toBeGreaterThan(0);
  });
});
