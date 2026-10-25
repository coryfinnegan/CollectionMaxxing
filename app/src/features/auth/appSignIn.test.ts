import { HttpResponse, http } from 'msw';
import { AppState, type AppStateStatus } from 'react-native';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { server } from '@/mocks/server';
import {
  installForegroundRegistration,
  refreshInstallRegistration,
  registerThisInstall,
  resetInstallRegistration,
} from './appSignIn';

const mockStore = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: async (k: string) => mockStore.get(k) ?? null,
  setItemAsync: async (k: string, v: string) => void mockStore.set(k, v),
}));

const API = 'http://api.test';
let onAuthLost: jest.Mock;

beforeEach(() => {
  mockStore.clear();
  onAuthLost = jest.fn();
  configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok'), onAuthLost });
  resetInstallRegistration();
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});
afterEach(() => {
  resetAuth();
  jest.restoreAllMocks();
});

describe('registering this iPhone after sign-in', () => {
  it('posts the device name with the per-install id, and keeps the same id next time', async () => {
    const seen: { body: unknown; installId: string | null }[] = [];
    server.use(
      http.post(`${API}/api/v1/me/app-sign-ins`, async ({ request }) => {
        seen.push({ body: await request.json(), installId: request.headers.get('X-Install-Id') });
        return HttpResponse.json({ id: 'x' }, { status: 201 });
      }),
    );

    await registerThisInstall();
    await registerThisInstall();

    expect(seen[0].body).toEqual({ deviceName: 'iPhone' });
    expect(seen[0].installId).toMatch(/^[0-9a-f-]{36}$/);
    expect(seen[1].installId).toBe(seen[0].installId);
  });

  it('ignores a 404 while the server has no such route yet', async () => {
    server.use(http.post(`${API}/api/v1/me/app-sign-ins`, () => HttpResponse.json({ status: 404 }, { status: 404 })));
    await expect(registerThisInstall()).resolves.toBeUndefined();
    expect(onAuthLost).not.toHaveBeenCalled();
  });

  it('a server error does not undo the sign-in', async () => {
    server.use(http.post(`${API}/api/v1/me/app-sign-ins`, () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await expect(registerThisInstall()).resolves.toBeUndefined();
    expect(onAuthLost).not.toHaveBeenCalled();
  });
});

const DAY_MS = 24 * 60 * 60 * 1000;

/** Counts registration POSTs; the server answers 201. */
const countRegistrations = () => {
  let posts = 0;
  server.use(
    http.post(`${API}/api/v1/me/app-sign-ins`, () => {
      posts += 1;
      return HttpResponse.json({ id: 'x' }, { status: 201 });
    }),
  );
  return () => posts;
};

describe('re-registering to keep last_seen_at fresh', () => {
  // The clock starts at 0 (never registered), so the tests pin "now" to realistic timestamps.
  let now: number;
  beforeEach(() => {
    now = Date.now();
  });

  it('registers when the install never registered', async () => {
    const posts = countRegistrations();

    await refreshInstallRegistration(now);

    expect(posts()).toBe(1);
  });

  it('skips when the last registration is under a day old', async () => {
    const posts = countRegistrations();

    await refreshInstallRegistration(now);
    await refreshInstallRegistration(now + DAY_MS);

    expect(posts()).toBe(1);
  });

  it('registers again when the last registration is more than a day old', async () => {
    const posts = countRegistrations();

    await refreshInstallRegistration(now);
    await refreshInstallRegistration(now + DAY_MS + 1);

    expect(posts()).toBe(2);
  });

  it('a failed registration leaves the day clock alone, so the next one retries', async () => {
    let posts = 0;
    let fail = true;
    server.use(
      http.post(`${API}/api/v1/me/app-sign-ins`, () => {
        posts += 1;
        return fail ? HttpResponse.json({ status: 500 }, { status: 500 }) : HttpResponse.json({ id: 'x' }, { status: 201 });
      }),
    );

    await refreshInstallRegistration(now);
    fail = false;
    await refreshInstallRegistration(now + 1);

    expect(posts).toBe(2);
  });

  it('signing in counts as a registration for the day clock', async () => {
    const posts = countRegistrations();

    await registerThisInstall();
    await refreshInstallRegistration();

    expect(posts()).toBe(1);
  });
});

describe('foreground returns', () => {
  let foreground: ((state: AppStateStatus) => void) | null = null;
  let removeListener: jest.Mock;

  beforeEach(() => {
    foreground = null;
    removeListener = jest.fn();
    jest
      .spyOn(AppState, 'addEventListener')
      .mockClear()
      .mockImplementation((type, handler) => {
        expect(type).toBe('change');
        foreground = handler;
        return { remove: removeListener };
      });
  });

  const comeToForeground = async () => {
    foreground?.('active');
    await new Promise((r) => setTimeout(r, 50));
  };

  it('registers when the last registration is more than a day old', async () => {
    const posts = countRegistrations();
    await refreshInstallRegistration(Date.now() - DAY_MS - 1);
    installForegroundRegistration();

    await comeToForeground();

    expect(posts()).toBe(2);
  });

  it('registers nothing when the last registration is under a day old', async () => {
    const posts = countRegistrations();
    await refreshInstallRegistration(Date.now());
    installForegroundRegistration();

    await comeToForeground();

    expect(posts()).toBe(1);
  });

  it('registers nothing when the app goes to the background', async () => {
    const posts = countRegistrations();
    installForegroundRegistration();

    foreground?.('background');
    await new Promise((r) => setTimeout(r, 50));

    expect(posts()).toBe(0);
  });

  it('registers nothing when the phone is signed out', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth(null), onAuthLost });
    const posts = countRegistrations();
    installForegroundRegistration();

    await comeToForeground();

    expect(posts()).toBe(0);
  });

  it('unsubscribes when the app no longer needs it', () => {
    const stop = installForegroundRegistration();

    stop();

    expect(removeListener).toHaveBeenCalledTimes(1);
  });

  it('installs no listener on web', () => {
    configureAuth({ platform: 'web', native: new MemoryNativeAuth('tok'), onAuthLost });

    installForegroundRegistration();

    expect(AppState.addEventListener).not.toHaveBeenCalled();
  });
});
