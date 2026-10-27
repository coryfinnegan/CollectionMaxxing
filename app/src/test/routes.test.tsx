// Render smoke test: every route in the navigation map renders. Tab screens show
// their content title (native tabs have no navigator header); pushed screens
// show their content, because their native headers are not text in this environment.
import { act, waitFor } from '@testing-library/react-native';
import { renderRouter, screen } from 'expo-router/testing-library';
import { HttpResponse, http } from 'msw';
import * as sdk from 'react-native-auth0';
import { resetAuth } from '@/lib/auth/session';
import { server } from '@/mocks/server';

// The Jest stand-in for react-native-auth0 (app/__mocks__): the real sign-in code runs on top of it.
const { mockAuth0, resetMockAuth0 } = sdk as unknown as { mockAuth0: Record<string, Record<string, jest.Mock>>; resetMockAuth0: () => void };

const mockStore = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: async (k: string) => mockStore.get(k) ?? null,
  setItemAsync: async (k: string, v: string) => void mockStore.set(k, v),
}));

const routes: [url: string, expected: string][] = [
  ['/library', 'Library'],
  ['/scan', 'Review'],
  ['/me', 'Me'],
  ['/me/locations', 'Where things live: rooms, shelves, boxes.'],
  ['/me/export', 'Download everything in your library as files you can keep.'],
  ['/me/devices', 'Where you are signed in.'],
  ['/me/sign-in-methods', 'The accounts you can use to sign in.'],
  ['/me/deleted', 'Deleted items stay here for 30 days.'],
  ['/me/delete', "Delete your library and photos. This can't be undone."],
  ['/wishlist', 'Blade Runner'],
  ['/lent-out', 'GoldenEye 007'],
  ['/members', 'Cory (you)'],
  ['/settings', 'Recognition, appearance and security on this iPhone.'],
  ['/about', 'Version, privacy and credits.'],
  ['/item/42', "Couldn't load this item"],
  ['/item/42/edit', "Couldn't load this item"],
  ['/scanner', 'Review'],
  ['/review', 'Nothing to review.'],
  ['/result/7', "That result isn't here any more"],
];

describe('routes', () => {
  beforeEach(() => {
    mockStore.clear();
    resetMockAuth0();
  });
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

describe('start-up registration', () => {
  beforeEach(() => {
    mockStore.clear();
    resetMockAuth0();
  });
  afterEach(() => resetAuth());

  type Registration = 'ok' | 'failing' | 'offline';

  /** Starts the app signed in, answering the registration POST as told and recording every attempt. */
  const startSignedIn = async (registration: Registration, seen: unknown[]) => {
    mockAuth0.credentialsManager.hasValidCredentials.mockResolvedValue(true);
    server.use(
      http.post('*/api/v1/me/app-sign-ins', async ({ request }) => {
        seen.push(await request.json());
        if (registration === 'failing') return HttpResponse.json({ status: 500 }, { status: 500 });
        if (registration === 'offline') return HttpResponse.error();
        return HttpResponse.json({ id: 'x' }, { status: 201 });
      }),
    );
    await renderRouter('./src/app', { initialUrl: '/' });
  };

  // renderRouter leaves fake timers on, so a bare sleep would hang: advance the clock instead.
  const settle = () =>
    act(async () => {
      jest.advanceTimersByTime(1000);
    });

  it('registers this iPhone once when start-up finds a working sign-in', async () => {
    const seen: unknown[] = [];
    await startSignedIn('ok', seen);

    expect((await screen.findAllByText('Library')).length).toBeGreaterThan(0);
    await waitFor(() => expect(seen).toHaveLength(1));
    // Nothing follows the first registration with a second.
    await settle();
    expect(seen).toHaveLength(1);
  });

  it('a failing start-up registration still opens the library and keeps the sign-in', async () => {
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      const seen: unknown[] = [];
      await startSignedIn('failing', seen);

      expect((await screen.findAllByText('Library')).length).toBeGreaterThan(0);
      await waitFor(() => expect(seen).toHaveLength(1));
      // The phone stays signed in after the registration settles: no trip to Sign in.
      await settle();
      expect(mockAuth0.credentialsManager.clearCredentials).not.toHaveBeenCalled();
      expect(screen.queryByText('Sign in to your library.')).toBeNull();
    } finally {
      warn.mockRestore();
    }
  });

  it('an offline start-up registration still opens the library and keeps the sign-in', async () => {
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      const seen: unknown[] = [];
      await startSignedIn('offline', seen);

      expect((await screen.findAllByText('Library')).length).toBeGreaterThan(0);
      await waitFor(() => expect(seen).toHaveLength(1));
      await settle();
      expect(mockAuth0.credentialsManager.clearCredentials).not.toHaveBeenCalled();
      expect(screen.queryByText('Sign in to your library.')).toBeNull();
    } finally {
      warn.mockRestore();
    }
  });
});
