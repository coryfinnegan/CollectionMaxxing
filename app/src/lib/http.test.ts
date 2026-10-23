import { HttpResponse, http } from 'msw';
import { server } from '@/mocks/server';
import { ApiError, customFetch } from './http';
import { PasskeyCancelledError } from './auth/errors';
import { AuthLostError, MemoryNativeAuth } from './auth/native';
import { configureAuth, resetAuth } from './auth/session';

const API = 'http://api.test';
const problem = (status: number, body: object = {}) =>
  HttpResponse.json({ status, ...body }, { status, headers: { 'Content-Type': 'application/problem+json' } });

afterEach(() => resetAuth());

describe('native client', () => {
  /** Hands out `tokens` in order, remembers whether a refresh was forced. */
  const tokens = (...values: string[]) => {
    const native = new MemoryNativeAuth();
    const getAccessToken = jest.fn(async (forceRefresh?: boolean) => {
      void forceRefresh;
      return values.shift() ?? 'last';
    });
    native.getAccessToken = getAccessToken;
    return { native, getAccessToken };
  };
  const ping = (status: number, goodToken = 'fresh') =>
    http.get(`${API}/api/v1/ping`, ({ request }) =>
      request.headers.get('Authorization') === `Bearer ${goodToken}` ? HttpResponse.json({ ok: true }) : problem(status),
    );

  it('a 401 gets one forced refresh and one retry with the new token', async () => {
    server.use(ping(401));
    const { native, getAccessToken } = tokens('stale', 'fresh', 'fresh');
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native, onAuthLost });

    const result = await customFetch<{ status: number }>('/api/v1/ping');

    expect(result.status).toBe(200);
    expect(getAccessToken.mock.calls.map((c) => c[0] ?? false)).toEqual([false, true, false]);
    expect(onAuthLost).not.toHaveBeenCalled();
  });

  it('five concurrent 401s cause one refresh and all five requests then succeed', async () => {
    server.use(ping(401));
    let current = 'stale'; // like the Credentials Manager: a forced refresh replaces what later calls return
    const getAccessToken = jest.fn(async (forceRefresh?: boolean) => {
      if (forceRefresh) current = 'fresh';
      return current;
    });
    configureAuth({ platform: 'native', native: Object.assign(new MemoryNativeAuth(), { getAccessToken }) });

    const results = await Promise.all(Array.from({ length: 5 }, () => customFetch<{ status: number }>('/api/v1/ping')));

    expect(results.map((r) => r.status)).toEqual([200, 200, 200, 200, 200]);
    expect(getAccessToken.mock.calls.filter((c) => c[0] === true)).toHaveLength(1);
  });

  it('a refresh Auth0 rejects sends the person to Sign in', async () => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(401)));
    const native = Object.assign(new MemoryNativeAuth(), {
      getAccessToken: jest.fn(async (force?: boolean) => {
        if (force) throw new AuthLostError();
        return 'stale';
      }),
    });
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native, onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthLost).toHaveBeenCalledWith('login');
  });

  it('a 401 that survives the retry sends the person to Sign in', async () => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(401)));
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok'), onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthLost).toHaveBeenCalledWith('login');
  });

  it('a 401 whose refresh fails on the network keeps the credentials and does not send the person to Sign in', async () => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(401)));
    const native = Object.assign(new MemoryNativeAuth(), {
      getAccessToken: jest.fn(async (force?: boolean) => {
        if (force) throw new Error('Network request failed');
        return 'stale';
      }),
    });
    const clear = jest.spyOn(native, 'clear');
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native, onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthLost).not.toHaveBeenCalled();
    expect(clear).not.toHaveBeenCalled();
  });

  it.each([429, 500, 503])('a %i never clears the credentials or sends the person to Sign in', async (status) => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(status)));
    const native = new MemoryNativeAuth('tok');
    const clear = jest.spyOn(native, 'clear');
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native, onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toMatchObject({ status });
    expect(onAuthLost).not.toHaveBeenCalled();
    expect(clear).not.toHaveBeenCalled();
    expect(native.token).toBe('tok');
  });

  it('a network error while asking for the token surfaces as it is and keeps the credentials', async () => {
    const native = Object.assign(new MemoryNativeAuth(), { getAccessToken: jest.fn().mockRejectedValue(new Error('Network request failed')) });
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native, onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toThrow('Network request failed');
    expect(onAuthLost).not.toHaveBeenCalled();
  });

  it('403 account.not_linked opens the not-linked page', async () => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(403, { code: 'account.not_linked' })));
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok'), onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toMatchObject({ status: 403 });
    expect(onAuthLost).toHaveBeenCalledWith('not-linked');
  });

  it('any other 403 is only an error', async () => {
    server.use(http.get(`${API}/api/v1/ping`, () => problem(403, { code: 'role.forbidden' })));
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok'), onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toMatchObject({ status: 403 });
    expect(onAuthLost).not.toHaveBeenCalled();
  });

  it('sends the bearer token, no CSRF header and no cookies', async () => {
    let seen: Request | undefined;
    server.use(
      http.post(`${API}/api/v1/things`, ({ request }) => {
        seen = request;
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });

    await customFetch('/api/v1/things', { method: 'POST', body: '{}' });

    expect(seen?.headers.get('Authorization')).toBe('Bearer tok');
    expect(seen?.headers.get('X-CSRF')).toBeNull();
    expect(seen?.credentials).not.toBe('include');
  });

  it('does not refresh for a failed sign-in request', async () => {
    const { native, getAccessToken } = tokens('tok');
    configureAuth({ platform: 'native', native });
    server.use(http.post(`${API}/account/login`, () => problem(401)));

    await expect(customFetch('/account/login', { method: 'POST', body: '{}' })).rejects.toBeInstanceOf(ApiError);
    expect(getAccessToken.mock.calls.filter((c) => c[0] === true)).toHaveLength(0);
  });
});

describe('web client', () => {
  it('sends X-CSRF: 1 and cookies on writes, and only cookies on reads', async () => {
    const seen: Request[] = [];
    server.use(
      http.get(`${API}/api/v1/ping`, ({ request }) => {
        seen.push(request);
        return HttpResponse.json({});
      }),
      http.post(`${API}/api/v1/ping`, ({ request }) => {
        seen.push(request);
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    configureAuth({ platform: 'web' });

    await customFetch('/api/v1/ping');
    await customFetch('/api/v1/ping', { method: 'POST', body: '{}' });

    expect(seen[0].headers.get('X-CSRF')).toBeNull();
    expect(seen[1].headers.get('X-CSRF')).toBe('1');
    expect(seen.map((r) => r.credentials)).toEqual(['include', 'include']);
    expect(seen[1].headers.get('Authorization')).toBeNull();
  });

  const reauthProblem = () =>
    problem(401, { type: 'https://collectionmaxxing.app/problems/reauth_required', title: "Confirm it's you" });

  it('reauth_required triggers one passkey prompt and one retry of the original request', async () => {
    let calls = 0;
    server.use(
      http.post(`${API}/api/v1/me/devices/pair`, () => (++calls === 1 ? reauthProblem() : HttpResponse.json({ ok: 1 }, { status: 201 }))),
    );
    const reauth = jest.fn().mockResolvedValue(undefined);
    configureAuth({ platform: 'web', reauth });

    const result = await customFetch<{ status: number }>('/api/v1/me/devices/pair', { method: 'POST' });

    expect(result.status).toBe(201);
    expect(reauth).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2);
  });

  it('a cancelled passkey prompt rejects with the cancel error and does not retry', async () => {
    let calls = 0;
    server.use(
      http.post(`${API}/api/v1/me/devices/pair`, () => {
        calls++;
        return reauthProblem();
      }),
    );
    configureAuth({ platform: 'web', reauth: () => Promise.reject(new PasskeyCancelledError()) });

    await expect(customFetch('/api/v1/me/devices/pair', { method: 'POST' })).rejects.toBeInstanceOf(PasskeyCancelledError);
    expect(calls).toBe(1);
  });

  it('a second reauth_required after the retry is surfaced, not looped', async () => {
    let calls = 0;
    server.use(
      http.post(`${API}/api/v1/me/devices/pair`, () => {
        calls++;
        return reauthProblem();
      }),
    );
    configureAuth({ platform: 'web', reauth: jest.fn().mockResolvedValue(undefined) });

    await expect(customFetch('/api/v1/me/devices/pair', { method: 'POST' })).rejects.toBeInstanceOf(ApiError);
    expect(calls).toBe(2);
  });

  it('a plain 401 sends the person to sign in', async () => {
    server.use(http.get(`${API}/api/v1/me`, () => problem(401)));
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'web', onAuthLost });

    await expect(customFetch('/api/v1/me')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthLost).toHaveBeenCalledWith('login');
  });
});
