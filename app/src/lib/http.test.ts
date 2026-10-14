import { HttpResponse, http } from 'msw';
import { server } from '@/mocks/server';
import { ApiError, customFetch } from './http';
import { PasskeyCancelledError } from './auth/errors';
import { configureAuth, resetAuth, startSession } from './auth/session';
import { MemoryTokenStore } from './auth/tokenStore';

const API = 'http://api.test';
const problem = (status: number, body: object = {}) =>
  HttpResponse.json({ status, ...body }, { status, headers: { 'Content-Type': 'application/problem+json' } });

afterEach(() => resetAuth());

describe('native client', () => {
  const paired = () =>
    new MemoryTokenStore({ refreshToken: 'refresh-1', serverUrl: API });

  it('five concurrent 401s cause one refresh and all five requests then succeed', async () => {
    let refreshCalls = 0;
    server.use(
      http.post(`${API}/connect/token`, async () => {
        refreshCalls++;
        await new Promise((r) => setTimeout(r, 20));
        return HttpResponse.json({ access_token: 'fresh', refresh_token: 'refresh-2' });
      }),
      http.get(`${API}/api/v1/ping`, ({ request }) =>
        request.headers.get('Authorization') === 'Bearer fresh' ? HttpResponse.json({ ok: true }) : problem(401),
      ),
    );
    const store = paired();
    configureAuth({ platform: 'native', tokenStore: store });
    // Prime an expired access token, as if the app had been open for a while.
    await startSession({ accessToken: 'stale', refreshToken: 'refresh-1', serverUrl: API });

    const results = await Promise.all(Array.from({ length: 5 }, () => customFetch<{ status: number }>('/api/v1/ping')));

    expect(results.map((r) => r.status)).toEqual([200, 200, 200, 200, 200]);
    expect(refreshCalls).toBe(1);
    expect((await store.load())?.refreshToken).toBe('refresh-2');
  });

  it('a failed refresh clears the token store and routes to pairing', async () => {
    server.use(
      http.post(`${API}/connect/token`, () => HttpResponse.json({ error: 'invalid_grant' }, { status: 400 })),
      http.get(`${API}/api/v1/ping`, () => problem(401)),
    );
    const store = paired();
    const onAuthLost = jest.fn();
    configureAuth({ platform: 'native', tokenStore: store, onAuthLost });

    await expect(customFetch('/api/v1/ping')).rejects.toBeInstanceOf(ApiError);

    expect(await store.load()).toBeNull();
    expect(onAuthLost).toHaveBeenCalledWith('pair');
  });

  it('sends the bearer token, no CSRF header and no cookies', async () => {
    let seen: Request | undefined;
    server.use(
      http.post(`${API}/connect/token`, () => HttpResponse.json({ access_token: 'tok', refresh_token: 'refresh-2' })),
      http.post(`${API}/api/v1/things`, ({ request }) => {
        seen = request;
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    configureAuth({ platform: 'native', tokenStore: paired() });

    await customFetch('/api/v1/things', { method: 'POST', body: '{}' });

    expect(seen?.headers.get('Authorization')).toBe('Bearer tok');
    expect(seen?.headers.get('X-CSRF')).toBeNull();
    expect(seen?.credentials).not.toBe('include');
  });

  it('does not refresh for a failed sign-in request', async () => {
    let refreshCalls = 0;
    server.use(
      http.post(`${API}/connect/token`, () => {
        refreshCalls++;
        return HttpResponse.json({}, { status: 400 });
      }),
    );
    configureAuth({ platform: 'native', tokenStore: new MemoryTokenStore() });
    server.use(http.post(`${API}/account/login`, () => problem(401)));

    await expect(customFetch('/account/login', { method: 'POST', body: '{}' })).rejects.toBeInstanceOf(ApiError);
    expect(refreshCalls).toBe(0);
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
