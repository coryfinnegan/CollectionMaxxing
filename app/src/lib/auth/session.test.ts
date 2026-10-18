import { HttpResponse, http } from 'msw';
import { server } from '@/mocks/server';
import { baseUrl, configureAuth, refreshAccessToken, resetAuth } from './session';
import { MemoryTokenStore } from './tokenStore';

const API = 'http://api.test';

beforeEach(() => {
  jest.spyOn(console, 'warn').mockImplementation(() => {}); // "Could not reach the server" is expected in the network-error case
});

afterEach(() => {
  resetAuth();
  jest.restoreAllMocks();
});

describe('refreshing the session (native)', () => {
  const setup = () => {
    const store = new MemoryTokenStore({ refreshToken: 'refresh-1', serverUrl: API });
    const onAuthLost = jest.fn();
    const clear = jest.spyOn(store, 'clear');
    configureAuth({ platform: 'native', tokenStore: store, onAuthLost });
    return { store, onAuthLost, clear };
  };

  it.each([
    ['a 429 with Retry-After', () => new HttpResponse(null, { status: 429, headers: { 'Retry-After': '30' } })],
    ['a 429 without Retry-After', () => new HttpResponse(null, { status: 429 })],
    ['a 503', () => new HttpResponse(null, { status: 503 })],
    ['a 400 that is not invalid_grant', () => HttpResponse.json({ error: 'invalid_request' }, { status: 400 })],
    ['a network error', () => HttpResponse.error()],
  ])('%s keeps the credentials and does not send the person to pairing', async (_name, respond) => {
    server.use(http.post(`${API}/connect/token`, respond));
    const { store, onAuthLost, clear } = setup();

    await expect(refreshAccessToken()).resolves.toBe(false);

    expect(clear).not.toHaveBeenCalled();
    expect(onAuthLost).not.toHaveBeenCalled();
    expect(await store.load()).toEqual({ refreshToken: 'refresh-1', serverUrl: API });
  });

  it.each([
    ['a 400 invalid_grant', () => HttpResponse.json({ error: 'invalid_grant' }, { status: 400 })],
    ['a 401', () => new HttpResponse(null, { status: 401 })],
  ])('%s means the device was revoked: credentials cleared, back to pairing', async (_name, respond) => {
    server.use(http.post(`${API}/connect/token`, respond));
    const { store, onAuthLost } = setup();

    await expect(refreshAccessToken()).resolves.toBe(false);

    expect(await store.load()).toBeNull();
    expect(onAuthLost).toHaveBeenCalledWith('pair');
  });

  it('after a 429 it waits for Retry-After before asking again, then retries and succeeds', async () => {
    const now = jest.spyOn(Date, 'now');
    now.mockReturnValue(Date.parse('2026-01-01T00:00:00Z'));
    let calls = 0;
    server.use(
      http.post(`${API}/connect/token`, () => {
        calls++;
        return calls === 1
          ? new HttpResponse(null, { status: 429, headers: { 'Retry-After': '30' } })
          : HttpResponse.json({ access_token: 'fresh', refresh_token: 'refresh-2' });
      }),
    );
    const { store } = setup();

    await expect(refreshAccessToken()).resolves.toBe(false);
    now.mockReturnValue(Date.parse('2026-01-01T00:00:10Z'));
    await expect(refreshAccessToken()).resolves.toBe(false);
    expect(calls).toBe(1); // still inside the 30 seconds: no second request

    now.mockReturnValue(Date.parse('2026-01-01T00:00:31Z'));
    await expect(refreshAccessToken()).resolves.toBe(true);
    expect(calls).toBe(2);
    expect((await store.load())?.refreshToken).toBe('refresh-2');
    now.mockRestore();
  });
});

describe('baseUrl', () => {
  const original = process.env.EXPO_PUBLIC_API_URL;
  const originalLocation = Object.getOwnPropertyDescriptor(globalThis, 'location');

  const setLocation = (origin: string | undefined) =>
    Object.defineProperty(globalThis, 'location', {
      value: origin ? { origin } : undefined,
      configurable: true,
      writable: true,
    });

  afterEach(() => {
    if (original === undefined) delete process.env.EXPO_PUBLIC_API_URL;
    else process.env.EXPO_PUBLIC_API_URL = original;
    if (originalLocation) Object.defineProperty(globalThis, 'location', originalLocation);
    else delete (globalThis as { location?: unknown }).location;
  });

  it('on the web, with no address configured, is the page’s own origin', () => {
    delete process.env.EXPO_PUBLIC_API_URL;
    setLocation('https://pc.tailnet.ts.net');
    configureAuth({ platform: 'web' });

    expect(baseUrl()).toBe('https://pc.tailnet.ts.net');
  });

  it('on the web, a configured address wins', () => {
    process.env.EXPO_PUBLIC_API_URL = 'http://127.0.0.1:9999';
    setLocation('https://pc.tailnet.ts.net');
    configureAuth({ platform: 'web' });

    expect(baseUrl()).toBe('http://127.0.0.1:9999');
  });

  it('on iOS, with no address configured, keeps the local default and never the page origin', () => {
    delete process.env.EXPO_PUBLIC_API_URL;
    setLocation('https://pc.tailnet.ts.net');
    configureAuth({ platform: 'native' });

    expect(baseUrl()).toBe('http://localhost:5480');
  });
});
