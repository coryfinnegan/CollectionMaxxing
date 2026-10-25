// Render smoke test: every route in the navigation map renders. Tab screens show
// their content title (native tabs have no navigator header); pushed screens
// show their content, because their native headers are not text in this environment.
import { renderRouter, screen } from 'expo-router/testing-library';
import * as sdk from 'react-native-auth0';
import { resetAuth } from '@/lib/auth/session';

// The Jest stand-in for react-native-auth0 (app/__mocks__): the real sign-in code runs on top of it.
const { mockAuth0, resetMockAuth0 } = sdk as unknown as { mockAuth0: Record<string, Record<string, jest.Mock>>; resetMockAuth0: () => void };

const routes: [url: string, expected: string][] = [
  ['/library', 'Library'],
  ['/scan', 'Scan'],
  ['/me', 'Me'],
  ['/me/locations', 'Where things live: rooms, shelves, boxes.'],
  ['/me/export', 'Download everything in your library as files you can keep.'],
  ['/me/devices', 'Where you are signed in.'],
  ['/me/delete', "Delete your library and photos. This can't be undone."],
  ['/wishlist', 'Wishlist'],
  ['/lent-out', 'Lent out'],
  ['/members', 'Cory (you)'],
  ['/settings', 'Recognition and security on this iPhone.'],
  ['/about', 'Version, privacy and credits.'],
  ['/item/42', "Couldn't load this item"],
  ['/item/42/edit', "Couldn't load this item"],
  ['/scanner', 'Close'],
  ['/review', 'Nothing to review.'],
  ['/result/7', "That result isn't here any more"],
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
