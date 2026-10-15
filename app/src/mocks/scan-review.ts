// Mock Review endpoints (docs/04): choose an alternative or change copies, re-search a not-found, commit, abandon.
// Registered before the scan handlers, so these answer PATCH and DELETE on a candidate or session first.
// Like the real API: the commit is one transaction (a failure creates nothing), takes an Idempotency-Key and
// replays the same answer for it, and a viewer cannot change anything (403).
import { HttpResponse, http } from 'msw';
import type { CandidateDto, CommitResultDto, ItemDto, ReleaseDto } from '@/api/generated/models';
import { alternativesOf } from '@/features/review/grouping';
import { newMockId } from './data';
import { itemsState, type MockItem } from './items';
import { scanMock } from './scan';

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

export const reviewMock = {
  /** Set to make the next commit fail as a whole (nothing is created), like a database rollback. */
  failNextCommit: false,
  /** Every commit request the server received. */
  commits: [] as { sessionId: string; key: string | null }[],
  replays: new Map<string, CommitResultDto>(),
  reset() {
    this.failNextCommit = false;
    this.commits = [];
    this.replays = new Map();
  },
};

const resolve = (c: CandidateDto, release: ReleaseDto) => {
  const owned = Object.values(scanMock.catalogue).filter((e) => e.release.id === release.id).flatMap((e) => e.owned);
  Object.assign(c, {
    resolution: 'matched',
    score: 1,
    release,
    alternatives: null,
    alreadyOwnedCount: owned.length,
    ownedAt: owned.map((o) => o.locationPath),
  } satisfies Partial<CandidateDto>);
};

const makeItem = (release: ReleaseDto): MockItem => {
  const now = new Date().toISOString();
  const item: ItemDto = {
    id: newMockId(),
    releaseId: release.id,
    status: 'owned',
    condition: null,
    completeness: null,
    locationId: null,
    notes: null,
    acquiredOn: null,
    lentTo: null,
    lentOn: null,
    customTitle: null,
    customCoverAssetId: null,
    coverChoice: 'release',
    addedVia: 'barcode',
    tagIds: [],
    createdAt: now,
    createdBy: '22222222-2222-4222-8222-222222222222',
    updatedAt: now,
    deletedAt: null,
  };
  const work = { id: release.workId, kind: 'other' as const, title: release.title, originalYear: release.year, creators: [], genres: [], synopsis: null, coverUrl: null };
  return { item, work, release, version: 1 };
};

export const handlers = [
  http.patch('*/api/v1/scan-sessions/:id/candidates/:cid', async ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    const candidate = session?.candidates.find((c) => c.id === params.cid);
    if (!session || !candidate) return problem(404, 'Not found');
    const body = (await request.json()) as { decision?: CandidateDto['decision'] | null; releaseId?: string | null; copies?: number | null };
    if (body.releaseId) {
      const chosen = alternativesOf(candidate).find((r) => r.id === body.releaseId);
      if (!chosen) return problem(404, 'That is not one of the alternatives.');
      resolve(candidate, chosen);
      candidate.decision = 'accepted';
    }
    if (body.decision) candidate.decision = body.decision;
    if (body.copies) candidate.copies = body.copies;
    candidate.seq = ++session.seq;
    return HttpResponse.json(candidate);
  }),

  // MSW can't match the ":search" suffix as a literal path segment, so match on the whole tail.
  http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/candidates\/([^/]+):search$/, async ({ request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const parts = new URL(request.url).pathname.split('/');
    const sessionId = parts[parts.indexOf('scan-sessions') + 1];
    const candidateId = parts[parts.length - 1]?.replace(':search', '');
    const session = scanMock.sessions.get(String(sessionId));
    const candidate = session?.candidates.find((c) => c.id === candidateId);
    if (!session || !candidate) return problem(404, 'Not found');
    const text = (((await request.json()) as { text?: string | null }).text ?? '').trim().toLowerCase();
    if (!text) return problem(400, 'Type a title to search for.');
    const found = Object.values(scanMock.catalogue).find((e) => e.release.title.toLowerCase().includes(text));
    if (found) resolve(candidate, found.release);
    candidate.seq = ++session.seq;
    return HttpResponse.json(candidate);
  }),

  http.post('*/api/v1/scan-sessions/:id/commit', ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const key = request.headers.get('Idempotency-Key');
    reviewMock.commits.push({ sessionId: session.id, key });
    if (!key) return problem(400, 'An Idempotency-Key header is required.');
    const replay = reviewMock.replays.get(`${session.id}:${key}`);
    if (replay) return HttpResponse.json(replay);
    if (reviewMock.failNextCommit) {
      reviewMock.failNextCommit = false;
      return problem(500, 'Something went wrong.');
    }
    if (session.status !== 'open') return problem(409, 'This scan was already finished.');
    const accepted = session.candidates.filter((c) => c.decision === 'accepted' && !c.createdItemId);
    if (accepted.length === 0) return problem(422, 'Nothing is ready to add.');

    const itemIds: string[] = [];
    let skipped = 0;
    for (const c of accepted) {
      if (!c.release) {
        skipped += 1;
        continue;
      }
      for (let copy = 0; copy < Math.max(1, c.copies); copy += 1) {
        const entry = makeItem(c.release);
        itemsState.entries.push(entry);
        itemIds.push(entry.item.id);
        c.createdItemId ??= entry.item.id;
      }
      c.seq = ++session.seq;
    }
    session.status = 'committed';
    const result: CommitResultDto = { created: itemIds.length, skipped, itemIds };
    reviewMock.replays.set(`${session.id}:${key}`, result);
    return HttpResponse.json(result);
  }),

  http.delete('*/api/v1/scan-sessions/:id', ({ params }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    session.status = 'abandoned';
    return new HttpResponse(null, { status: 204 });
  }),
];
