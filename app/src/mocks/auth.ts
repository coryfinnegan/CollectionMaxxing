// MSW mocks of the ID endpoints (setup, passkeys, token, devices, sessions). Magic values make each outcome reachable:
//   setup code   CM-SETUP-OK works, CM-EXPIRED is gone, anything else is wrong
//   invite       valid-invite works, expired-invite is gone, anything else is unknown
//   pairing      token pair-token-ok or code 123456 works, anything else is refused
import { HttpResponse, http } from 'msw';

export const MOCK_SETUP_CODE = 'CM-SETUP-OK';
export const MOCK_INVITE = 'valid-invite';
export const MOCK_PAIR_TOKEN = 'pair-token-ok';
export const MOCK_PAIR_CODE = '123456';
export const MOCK_SERVER_URL = 'https://pc.tailnet.ts.net';

export type MockSession = { id: string; deviceName: string; kind: string; createdAt: string; lastSeenAt: string | null; isCurrent: boolean };

const seedSessions = (): MockSession[] => [
  { id: 'a1111111-0000-4000-8000-000000000001', deviceName: 'This browser (Edge on Windows)', kind: 'web', createdAt: '2026-10-01T09:00:00Z', lastSeenAt: '2026-10-13T08:30:00Z', isCurrent: true },
  { id: 'a1111111-0000-4000-8000-000000000002', deviceName: 'Cory’s iPhone', kind: 'device', createdAt: '2026-10-02T18:00:00Z', lastSeenAt: '2026-10-12T20:15:00Z', isCurrent: false },
];

export const mockAuth = {
  sessions: seedSessions(),
  /** When true, device pairing answers 401 reauth_required until a passkey sign-in happens. */
  requireReauth: false,
  refreshCalls: 0,
};

export function resetMockAuth() {
  mockAuth.sessions = seedSessions();
  mockAuth.requireReauth = false;
  mockAuth.refreshCalls = 0;
}

const problem = (status: number, title: string, extra: object = {}) =>
  HttpResponse.json({ title, status, ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const creationOptions = { options: { rp: { id: 'pc.tailnet.ts.net', name: 'CollectionMaxxing' }, challenge: 'bW9jay1jaGFsbGVuZ2U', pubKeyCredParams: [{ type: 'public-key', alg: -7 }] } };
const signedIn = { userId: 'b2222222-0000-4000-8000-000000000001', displayName: 'Cory', libraryId: null };

const codeProblem = (code: string | undefined, ok: string, expired: string) => {
  if (code === ok) return null;
  if (code === expired) return problem(410, 'Gone', { code: 'code.expired' });
  return problem(400, 'Bad request', { code: 'code.invalid' });
};

export const handlers = [
  http.post('*/setup', async ({ request }) => {
    const body = (await request.json()) as { code?: string; displayName?: string };
    return codeProblem(body.code, MOCK_SETUP_CODE, 'CM-EXPIRED') ?? HttpResponse.json(creationOptions);
  }),

  http.post('*/account/passkey/creation-options', async ({ request }) => {
    const body = (await request.json()) as { setupCode?: string; inviteToken?: string };
    if (body.inviteToken === MOCK_INVITE) return HttpResponse.json(creationOptions);
    if (body.inviteToken === 'expired-invite') return problem(410, 'Gone', { code: 'invite.expired' });
    return problem(404, 'Not found');
  }),

  http.post('*/account/passkey/register', () => HttpResponse.json(signedIn, { status: 201 })),

  http.post('*/account/passkey/request-options', () => HttpResponse.json(creationOptions)),

  http.post('*/account/login', () => {
    mockAuth.requireReauth = false;
    return HttpResponse.json(signedIn);
  }),

  http.post('*/account/logout', () => new HttpResponse(null, { status: 204 })),

  http.get('*/api/v1/invites/:token', ({ params }) =>
    params.token === MOCK_INVITE ? HttpResponse.json({ ownerName: 'Cory', role: 'editor' }) : params.token === 'expired-invite' ? problem(410, 'Gone') : problem(404, 'Not found'),
  ),

  http.post('*/connect/token', async ({ request }) => {
    const form = new URLSearchParams(await request.text());
    if (form.get('grant_type') === 'refresh_token') {
      mockAuth.refreshCalls++;
      return form.get('refresh_token') === 'mock-refresh'
        ? HttpResponse.json({ access_token: 'mock-access', refresh_token: 'mock-refresh', token_type: 'Bearer', expires_in: 600 })
        : HttpResponse.json({ error: 'invalid_grant' }, { status: 400 });
    }
    const ok = form.get('pairing_token') === MOCK_PAIR_TOKEN || form.get('pairing_code') === MOCK_PAIR_CODE;
    return ok
      ? HttpResponse.json({ access_token: 'mock-access', refresh_token: 'mock-refresh', token_type: 'Bearer', expires_in: 600 })
      : HttpResponse.json({ error: 'invalid_grant' }, { status: 400 });
  }),

  http.post('*/api/v1/me/devices/pair', () => {
    if (mockAuth.requireReauth) {
      return problem(401, "Confirm it's you", { type: 'https://collectionmaxxing.app/problems/reauth_required', code: 'reauth_required', reauth: true });
    }
    return HttpResponse.json(
      { serverUrl: MOCK_SERVER_URL, token: MOCK_PAIR_TOKEN, code: MOCK_PAIR_CODE, expiresAt: new Date(Date.now() + 120_000).toISOString() },
      { status: 201 },
    );
  }),

  http.get('*/api/v1/me/sessions', () =>
    HttpResponse.json({ items: mockAuth.sessions.filter((s) => s.kind !== 'web'), nextCursor: null }),
  ),

  http.get('*/api/v1/me/web-sessions', () =>
    HttpResponse.json({ items: mockAuth.sessions.filter((s) => s.kind === 'web'), nextCursor: null }),
  ),

  http.delete('*/api/v1/me/web-sessions/:id', ({ params }) => {
    const found = mockAuth.sessions.some((s) => s.id === params.id && s.kind === 'web');
    if (!found) return problem(404, 'Not found');
    mockAuth.sessions = mockAuth.sessions.filter((s) => s.id !== params.id);
    return new HttpResponse(null, { status: 204 });
  }),

  http.delete('*/api/v1/me/web-sessions', () => {
    mockAuth.sessions = mockAuth.sessions.filter((s) => s.kind !== 'web');
    return new HttpResponse(null, { status: 204 });
  }),

  http.delete('*/api/v1/me/sessions/:id', ({ params }) => {
    const found = mockAuth.sessions.some((s) => s.id === params.id && s.kind !== 'web');
    if (!found) return problem(404, 'Not found');
    mockAuth.sessions = mockAuth.sessions.filter((s) => s.id !== params.id);
    return new HttpResponse(null, { status: 204 });
  }),
];
