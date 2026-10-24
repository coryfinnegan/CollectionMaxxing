import { HttpResponse, http } from 'msw';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { server } from '@/mocks/server';
import { registerThisInstall } from './appSignIn';

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
