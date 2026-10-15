// Mock members and invites (docs/04 "Libraries and members"). Magic values make each outcome reachable:
//   preview   token MOCK_INVITE_TOKEN works; every other token (bad, used, expired) answers the same 404
//   invites   creating one returns the link once; the list never carries a link
//   reauth    while mockAuth.requireReauth is true, creating an invite answers 401 reauth_required
import { HttpResponse, http } from 'msw';
import type { CreateInviteRequest, InviteDto, MemberDto, UpdateMemberRequest } from '@/api/generated/models';
import { mockAuth } from './auth';
import { mockMe } from './data';

export const MOCK_INVITE_TOKEN = 'valid-invite';
const DAY = 86_400_000;

const problem = (status: number, title: string, extra: object = {}) =>
  HttpResponse.json({ title, status, ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const seedMembers = (): MemberDto[] => [
  { userId: mockMe().id, displayName: 'Cory', role: 'owner', joinedAt: '2026-10-01T09:00:00Z', devices: ['Edge on Windows', 'iPhone'], lastSeenAt: new Date().toISOString() },
  { userId: 'c3333333-0000-4000-8000-000000000001', displayName: 'Sam', role: 'editor', joinedAt: '2026-10-05T09:00:00Z', devices: ['Safari on iPhone'], lastSeenAt: '2026-10-12T20:15:00Z' },
  { userId: 'c3333333-0000-4000-8000-000000000002', displayName: 'Robin', role: 'viewer', joinedAt: '2026-10-06T09:00:00Z', devices: [], lastSeenAt: null },
];

const seedInvites = (): InviteDto[] => [
  { id: 'd4444444-0000-4000-8000-000000000001', name: 'Alex', role: 'viewer', createdAt: '2026-10-14T09:00:00Z', expiresAt: new Date(Date.now() + 5 * DAY).toISOString(), acceptedBy: null, acceptedAt: null, revokedAt: null },
  { id: 'd4444444-0000-4000-8000-000000000002', name: 'Jo', role: 'editor', createdAt: '2026-09-01T09:00:00Z', expiresAt: '2026-09-08T09:00:00Z', acceptedBy: null, acceptedAt: null, revokedAt: null },
];

export const mockMembers: { members: MemberDto[]; invites: InviteDto[]; nextId: number } = { members: seedMembers(), invites: seedInvites(), nextId: 100 };

export function resetMembers() {
  mockMembers.members = seedMembers();
  mockMembers.invites = seedInvites();
  mockMembers.nextId = 100;
}

const base = '*/api/v1/libraries/:libraryId';

export const handlers = [
  http.get('*/api/v1/invites/:token', ({ params }) =>
    params.token === MOCK_INVITE_TOKEN
      ? HttpResponse.json({ ownerName: 'Cory', libraryName: 'Our library', role: 'editor', expiresAt: new Date(Date.now() + 3 * DAY).toISOString() })
      : problem(404, 'Not found'),
  ),

  http.get(`${base}/members`, () => HttpResponse.json({ items: mockMembers.members, nextCursor: null })),

  http.patch(`${base}/members/:userId`, async ({ params, request }) => {
    const body = (await request.json()) as UpdateMemberRequest;
    const member = mockMembers.members.find((m) => m.userId === params.userId);
    if (!member) return problem(404, 'Not found');
    if (member.role === 'owner' || !body.role || body.role === 'owner') return problem(409, 'The owner role cannot be changed here.');
    member.role = body.role;
    return HttpResponse.json(member);
  }),

  http.delete(`${base}/members/:userId`, ({ params }) => {
    const member = mockMembers.members.find((m) => m.userId === params.userId);
    if (!member) return problem(404, 'Not found');
    if (member.role === 'owner') return problem(409, 'The owner cannot be removed.');
    mockMembers.members = mockMembers.members.filter((m) => m !== member);
    return new HttpResponse(null, { status: 204 });
  }),

  http.get(`${base}/invites`, () => HttpResponse.json({ items: mockMembers.invites, nextCursor: null })),

  http.post(`${base}/invites`, async ({ request }) => {
    if (mockAuth.requireReauth) {
      return problem(401, "Confirm it's you", { type: 'https://collectionmaxxing.app/problems/reauth_required', code: 'reauth_required', reauth: true });
    }
    const body = (await request.json()) as CreateInviteRequest;
    const name = body.name?.trim();
    const role = body.role;
    if (!name) return problem(400, 'One or more validation errors occurred.', { errors: { name: ['Name is required.'] } });
    if (role !== 'editor' && role !== 'viewer') return problem(400, 'One or more validation errors occurred.', { errors: { role: ['Choose Editor or Viewer.'] } });
    mockMembers.nextId += 1;
    const invite: InviteDto = {
      id: `d4444444-0000-4000-8000-${String(mockMembers.nextId).padStart(12, '0')}`,
      name,
      role,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 7 * DAY).toISOString(),
      acceptedBy: null,
      acceptedAt: null,
      revokedAt: null,
    };
    mockMembers.invites.push(invite);
    const link = `https://pc.tailnet.ts.net/join/${MOCK_INVITE_TOKEN}-${mockMembers.nextId}`;
    return HttpResponse.json({ invite, link, qrPayload: link }, { status: 201 });
  }),

  http.delete(`${base}/invites/:id`, ({ params }) => {
    const invite = mockMembers.invites.find((i) => i.id === params.id);
    if (!invite) return problem(404, 'Not found');
    mockMembers.invites = mockMembers.invites.filter((i) => i !== invite);
    return new HttpResponse(null, { status: 204 });
  }),
];
