import { AuthLostError, MemoryNativeAuth, type NativeAuth } from './native';
import { authHeaders, baseUrl, configureAuth, endSession, refreshAccessToken, resetAuth } from './session';

afterEach(() => resetAuth());

/** A NativeAuth whose token calls are scripted. */
const scripted = (getAccessToken: NativeAuth['getAccessToken']): NativeAuth => Object.assign(new MemoryNativeAuth(), { getAccessToken });

describe('credentials for a request (native)', () => {
  it('sends the Credentials Manager token as the bearer', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });
    expect(await authHeaders('GET')).toEqual({ Authorization: 'Bearer tok' });
  });

  it('with no credentials at all, sends the person to Sign in', async () => {
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: new MemoryNativeAuth(null), onAuthLost });

    expect(await authHeaders('GET')).toEqual({});
    expect(onAuthLost).toHaveBeenCalledWith('login');
  });

  it.each([
    ['a network error', new Error('Network request failed')],
    ['a timeout', new Error('timeout')],
    ['a 429', Object.assign(new Error('Too many requests'), { status: 429 })],
    ['a 503', Object.assign(new Error('Service unavailable'), { status: 503 })],
  ])('%s keeps the credentials, does not send the person to Sign in, and surfaces the error', async (_name, error) => {
    const onAuthLost = jest.fn();
    const native = scripted(() => Promise.reject(error));
    const clear = jest.spyOn(native, 'clear');
    configureAuth({ platform: 'native', native, onAuthLost });

    await expect(authHeaders('GET')).rejects.toBe(error);
    expect(onAuthLost).not.toHaveBeenCalled();
    expect(clear).not.toHaveBeenCalled();
  });
});

describe('refreshing after a 401 (native)', () => {
  it('forces one refresh shared by concurrent callers', async () => {
    const getAccessToken = jest.fn().mockResolvedValue('fresh');
    configureAuth({ platform: 'native', native: scripted(getAccessToken) });

    const tokens = await Promise.all([refreshAccessToken(), refreshAccessToken(), refreshAccessToken()]);

    expect(tokens).toEqual(['fresh', 'fresh', 'fresh']);
    expect(getAccessToken).toHaveBeenCalledTimes(1);
    expect(getAccessToken).toHaveBeenCalledWith(true);
  });

  it('a refresh Auth0 rejects sends the person to Sign in', async () => {
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: scripted(() => Promise.reject(new AuthLostError())), onAuthLost });

    expect(await refreshAccessToken()).toBeNull();
    expect(onAuthLost).toHaveBeenCalledWith('login');
  });

  it('a refresh that cannot run (offline, 5xx, 429) returns null and leaves the person signed in', async () => {
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: scripted(() => Promise.reject(new Error('Network request failed'))), onAuthLost });

    expect(await refreshAccessToken()).toBeNull();
    expect(onAuthLost).not.toHaveBeenCalled();
  });
});

it('ending the session clears the stored credentials', async () => {
  const native = new MemoryNativeAuth('tok');
  configureAuth({ platform: 'native', native });
  await endSession();
  expect(native.token).toBeNull();
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
