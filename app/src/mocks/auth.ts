// MSW mocks of the sign-in endpoints (docs/14). Sign-in itself is a browser navigation, so only its JSON helpers are mocked. Magic values:
//   setup code   CM-SETUP-OK works, CM-EXPIRED is gone, anything else is wrong
//   invite       valid-invite works, expired-invite is gone, anything else is unknown
//   pairing      token pair-token-ok or code 123456 works on /connect/token (the iPhone's old pairing, until its own task replaces it)
import { HttpResponse, http } from 'msw';
import type { AppSignInDto, SignInMethodDto } from '@/features/auth/contract';

export const MOCK_SETUP_CODE = 'CM-SETUP-OK';
export const MOCK_INVITE = 'valid-invite';
export const MOCK_PAIR_TOKEN = 'pair-token-ok';
export const MOCK_PAIR_CODE = '123456';
export const MOCK_SERVER_URL = 'https://pc.tailnet.ts.net';
/** Where the mocked sign-out sends the browser: back to our own sign-in page (the real one goes to Auth0 first). */
export const MOCK_LOGOUT_URL = '/login';

export type MockSession = { id: string; deviceName: string; kind: string; createdAt: string; lastSeenAt: string | null; isCurrent: boolean };

const seedSessions = (): MockSession[] => [
  { id: 'a1111111-0000-4000-8000-000000000001', deviceName: 'This browser (Edge on Windows)', kind: 'web', createdAt: '2026-10-01T09:00:00Z', lastSeenAt: '2026-10-13T08:30:00Z', isCurrent: true },
  { id: 'a1111111-0000-4000-8000-000000000003', deviceName: 'Safari on Mac', kind: 'web', createdAt: '2026-10-03T09:00:00Z', lastSeenAt: '2026-10-11T08:30:00Z', isCurrent: false },
];

const seedIphones = (): AppSignInDto[] => [
  { id: 'a1111111-0000-4000-8000-000000000002', deviceName: 'Cory’s iPhone', createdAt: '2026-10-02T18:00:00Z', lastSeenAt: '2026-10-12T20:15:00Z' },
];

const seedMethods = (): SignInMethodDto[] => [
  { id: 'c3333333-0000-4000-8000-000000000001', provider: 'google', email: 'cory@example.com', createdAt: '2026-10-01T09:00:00Z', lastUsedAt: '2026-10-13T08:30:00Z', isCurrent: true },
  { id: 'c3333333-0000-4000-8000-000000000002', provider: 'apple', email: null, createdAt: '2026-10-05T09:00:00Z', lastUsedAt: null, isCurrent: false },
];

export const mockAuth = {
  /** Browsers (web sessions). */
  sessions: seedSessions(),
  iphones: seedIphones(),
  methods: seedMethods(),
  /** When true, sensitive calls answer 401 reauth_required. */
  requireReauth: false,
  refreshCalls: 0,
};

export function resetMockAuth() {
  mockAuth.sessions = seedSessions();
  mockAuth.iphones = seedIphones();
  mockAuth.methods = seedMethods();
  mockAuth.requireReauth = false;
  mockAuth.refreshCalls = 0;
}

const problem = (status: number, title: string, extra: object = {}) =>
  HttpResponse.json({ title, status, ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const reauthProblem = () =>
  problem(401, "Confirm it's you", { type: 'https://collectionmaxxing.app/problems/reauth_required', code: 'reauth_required', reauth: true });

export const handlers = [
  http.post('*/setup', async ({ request }) => {
    const body = (await request.json()) as { code?: string };
    if (body.code === MOCK_SETUP_CODE) return HttpResponse.json({ next: '/account/login?returnUrl=%2Flibrary' });
    return body.code === 'CM-EXPIRED' ? problem(410, 'Gone', { code: 'code.expired' }) : problem(400, 'Bad request', { code: 'code.invalid' });
  }),

  http.post('*/account/logout', () => HttpResponse.json({ logoutUrl: MOCK_LOGOUT_URL })),

  http.get('*/api/v1/invites/:token', ({ params }) =>
    params.token === MOCK_INVITE ? HttpResponse.json({ ownerName: 'Cory', role: 'editor' }) : params.token === 'expired-invite' ? problem(410, 'Gone') : problem(404, 'Not found'),
  ),

  http.get('*/api/v1/me/sign-in-methods', () => HttpResponse.json({ items: mockAuth.methods, nextCursor: null })),

  http.delete('*/api/v1/me/sign-in-methods/:id', ({ params }) => {
    if (mockAuth.requireReauth) return reauthProblem();
    if (!mockAuth.methods.some((m) => m.id === params.id)) return problem(404, 'Not found');
    if (mockAuth.methods.length === 1) return problem(409, 'You need at least one way to sign in.', { code: 'account.last_sign_in_method' });
    mockAuth.methods = mockAuth.methods.filter((m) => m.id !== params.id);
    return new HttpResponse(null, { status: 204 });
  }),

  http.get('*/api/v1/me/app-sign-ins', () => HttpResponse.json({ items: mockAuth.iphones, nextCursor: null })),

  http.delete('*/api/v1/me/app-sign-ins', () => {
    mockAuth.iphones = [];
    return new HttpResponse(null, { status: 204 });
  }),

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
];
