// Mock account endpoints for the Me tab (docs/04 "Account and session"). Magic state makes each outcome reachable:
//   export   GET /me/export starts a job; each poll moves queued -> running -> ready (or failed when exportOutcome is 'failed')
//   delete   DELETE /me answers 401 reauth_required without a passkey credential, 409 while libraryHasMembers, else 204
//   bin      the deleted items list; restoring removes the row
//   revoke   POST /connect/revoke (unpairing this iPhone)
import { HttpResponse, http } from 'msw';
import type { DeletedItemDto, ExportDto } from '@/api/generated/models';

const DAY = 86_400_000;

const problem = (status: number, title: string, extra: object = {}) =>
  HttpResponse.json({ title, status, ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const seedBin = (): DeletedItemDto[] => [
  {
    id: 'e5555555-0000-4000-8000-000000000001',
    releaseId: 'e5555555-0000-4000-8000-0000000000a1',
    workId: 'e5555555-0000-4000-8000-0000000000b1',
    title: 'Aliens',
    kind: 'film',
    format: 'vhs',
    platformName: null,
    year: 1986,
    coverUrl: null,
    deletedAt: new Date(Date.now() - 2 * DAY).toISOString(),
    purgesAt: new Date(Date.now() + 28 * DAY).toISOString(),
  },
  {
    id: 'e5555555-0000-4000-8000-000000000002',
    releaseId: 'e5555555-0000-4000-8000-0000000000a2',
    workId: 'e5555555-0000-4000-8000-0000000000b2',
    title: 'Star Fox 64',
    kind: 'game',
    format: 'game_cartridge',
    platformName: 'Nintendo 64',
    year: 1997,
    coverUrl: null,
    deletedAt: new Date(Date.now() - 29 * DAY).toISOString(),
    purgesAt: new Date(Date.now() + 1 * DAY).toISOString(),
  },
];

export const mockAccount: {
  bin: DeletedItemDto[];
  exportOutcome: 'ready' | 'failed';
  exportPolls: number;
  exportStarts: number;
  libraryHasMembers: boolean;
  deleted: boolean;
  revoked: string[];
  /** The synced iPhone app (OPS-4); null until the deployer has synced one. */
  ipa: { sizeBytes: number; modifiedAt: string; sha256: string | null } | null;
} = { bin: seedBin(), exportOutcome: 'ready', exportPolls: 0, exportStarts: 0, libraryHasMembers: false, deleted: false, revoked: [], ipa: null };

export function resetMockAccount() {
  mockAccount.bin = seedBin();
  mockAccount.exportOutcome = 'ready';
  mockAccount.exportPolls = 0;
  mockAccount.exportStarts = 0;
  mockAccount.libraryHasMembers = false;
  mockAccount.deleted = false;
  mockAccount.revoked = [];
  mockAccount.ipa = null;
}

const JOB_ID = 'f6666666-0000-4000-8000-000000000001';

export const handlers = [
  http.get('*/api/v1/app/ipa/info', () =>
    mockAccount.ipa ? HttpResponse.json(mockAccount.ipa) : problem(404, 'Not found', { detail: "The iPhone app isn't here yet. It appears after the next release." }),
  ),

  http.get('*/api/v1/me/export', () => {
    mockAccount.exportStarts++;
    mockAccount.exportPolls = 0;
    const started: ExportDto = { jobId: JOB_ID, status: 'queued', downloadUrl: null };
    return HttpResponse.json(started, { status: 202 });
  }),

  // The poll states advance one step per request: queued, running, then the outcome.
  http.get('*/api/v1/me/export/:id', ({ params }) => {
    if (params.id !== JOB_ID) return problem(404, 'Not found');
    mockAccount.exportPolls++;
    const status = mockAccount.exportPolls === 1 ? 'queued' : mockAccount.exportPolls === 2 ? 'running' : mockAccount.exportOutcome;
    const body: ExportDto = {
      jobId: JOB_ID,
      status,
      downloadUrl: status === 'ready' ? `/api/v1/me/export/${JOB_ID}/download?token=mock-signed-token` : null,
    };
    return HttpResponse.json(body);
  }),

  http.delete('*/api/v1/me', async ({ request }) => {
    const body = (await request.json().catch(() => ({}))) as { credential?: unknown };
    if (!body.credential) {
      return problem(401, "Confirm it's you", { type: 'https://collectionmaxxing.app/problems/reauth_required', code: 'reauth_required', reauth: true });
    }
    if (mockAccount.libraryHasMembers) return problem(409, 'Your library still has members.', { code: 'library_has_members' });
    mockAccount.deleted = true;
    return new HttpResponse(null, { status: 204 });
  }),

  http.get(/\/api\/v1\/libraries\/[^/]+\/items:deleted$/, () => HttpResponse.json({ items: mockAccount.bin, nextCursor: null })),

  // Restores of items this file does not hold fall through to the Items mock.
  http.post(/\/api\/v1\/libraries\/[^/]+\/items\/([^/]+):restore$/, ({ request }) => {
    const id = new URL(request.url).pathname.split('/').pop()?.replace(':restore', '');
    const row = mockAccount.bin.find((d) => d.id === id);
    if (!row) return;
    mockAccount.bin = mockAccount.bin.filter((d) => d !== row);
    return HttpResponse.json({ id: row.id, releaseId: row.releaseId, deletedAt: null });
  }),

  http.post('*/connect/revoke', async ({ request }) => {
    const form = new URLSearchParams(await request.text());
    mockAccount.revoked.push(form.get('token') ?? '');
    return new HttpResponse(null, { status: 200 });
  }),
];
