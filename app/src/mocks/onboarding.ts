// Mock account and library endpoints from docs/15 "API contract". Magic values make each outcome reachable:
//   libraries   a new account has none; creating a fourth owned library answers 409 library.limit_reached
//   invites     token `valid-invite` (Editor, from Cory) and code ABCD-EFGH-JK (Viewer, from Sam) work; `taken-invite` is already a
//               member (409); every other link, token or code answers the same 404 invite.invalid
//   leave       the only owner of a library gets 409 library.last_owner; anyone else leaves
//   session     mockAccount.signedIn = false makes /me and the library routes answer 401 (an invite preview still works signed out)
import { HttpResponse, http } from 'msw';
import type { LibraryDto, LibraryRole, LibrarySummaryDto } from '@/api/generated/models';
import type { CreateLibraryRequest, InviteLookupRequest, InvitePreviewDto } from '@/features/onboarding/api';
import { mockAccount } from './data';

export const MOCK_CODE = 'ABCD-EFGH-JK';
const DAY = 86_400_000;

const problem = (status: number, title: string, code?: string, extra: object = {}) =>
  HttpResponse.json({ title, status, ...(code ? { code } : {}), ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const signedOut = () => problem(401, 'Unauthorized');

type Found = { token: string; ownerName: string; libraryName: string; role: LibraryRole };

/** What a pasted link, token or code means in the mock. Everything unknown is `null`. */
function lookup(invite: string): Found | null {
  const cleaned = invite.trim();
  const token = cleaned.includes('/join/') ? (cleaned.split('/join/')[1] ?? '').split(/[?#]/)[0]! : cleaned;
  const code = token.replace(/-/g, '').toUpperCase();
  if (token === 'valid-invite' || token === 'taken-invite') return { token, ownerName: 'Cory', libraryName: "Cory's library", role: 'editor' };
  if (code === MOCK_CODE.replace(/-/g, '')) return { token: MOCK_CODE, ownerName: 'Sam', libraryName: "Sam's library", role: 'viewer' };
  return null;
}

const preview = (found: Found): InvitePreviewDto => ({
  ownerName: found.ownerName,
  libraryName: found.libraryName,
  role: found.role,
  expiresAt: new Date(Date.now() + 3 * DAY).toISOString(),
});

export const handlers = [
  http.get('*/api/v1/libraries', () =>
    mockAccount.signedIn ? HttpResponse.json({ items: mockAccount.libraries, nextCursor: null }) : signedOut(),
  ),

  http.post('*/api/v1/libraries', async ({ request }) => {
    if (!mockAccount.signedIn) return signedOut();
    const body = (await request.json()) as CreateLibraryRequest;
    const name = body.name?.trim() ?? '';
    if (!name) return problem(400, 'Give it a name.', 'library.name_required', { errors: { name: ['Name is required.'] } });
    if (name.length > 80) return problem(400, 'Use 80 characters or fewer.', 'library.name_too_long', { errors: { name: ['Name is too long.'] } });
    if (mockAccount.libraries.filter((l) => l.role === 'owner').length >= 3) return problem(409, 'Limit reached', 'library.limit_reached');
    mockAccount.nextLibrary += 1;
    const id = `f1000000-0000-4000-8000-${String(mockAccount.nextLibrary).padStart(12, '0')}`;
    mockAccount.libraries.push({ id, name, role: 'owner' });
    const library: LibraryDto = { id, name, role: 'owner', createdAt: new Date().toISOString() };
    return HttpResponse.json(library, { status: 201, headers: { Location: `/api/v1/libraries/${id}` } });
  }),

  http.post('*/api/v1/invites\\:preview', async ({ request }) => {
    if (!mockAccount.signedIn) return signedOut();
    const found = lookup(((await request.json()) as InviteLookupRequest).invite ?? '');
    return found ? HttpResponse.json(preview(found)) : problem(404, 'Not found', 'invite.invalid');
  }),

  http.post('*/api/v1/invites\\:accept', async ({ request }) => {
    if (!mockAccount.signedIn) return signedOut();
    const found = lookup(((await request.json()) as InviteLookupRequest).invite ?? '');
    if (!found) return problem(404, 'Not found', 'invite.invalid');
    if (found.token === 'taken-invite') return problem(409, 'Already a member', 'invite.already_member');
    mockAccount.nextLibrary += 1;
    const joined: LibrarySummaryDto = {
      id: `f2000000-0000-4000-8000-${String(mockAccount.nextLibrary).padStart(12, '0')}`,
      name: found.libraryName,
      role: found.role,
    };
    mockAccount.libraries.push(joined);
    return HttpResponse.json(joined);
  }),

  http.delete('*/api/v1/libraries/:libraryId/membership', ({ params }) => {
    if (!mockAccount.signedIn) return signedOut();
    const mine = mockAccount.libraries.find((l) => l.id === params.libraryId);
    if (!mine) return problem(404, 'Not found');
    if (mine.role === 'owner') return problem(409, 'Last owner', 'library.last_owner');
    mockAccount.libraries = mockAccount.libraries.filter((l) => l !== mine);
    return new HttpResponse(null, { status: 204 });
  }),
];
