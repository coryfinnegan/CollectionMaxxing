// Mock Review endpoints (docs/04): choose an alternative or change copies, re-search a not-found, commit, undo a commit, abandon.
// Registered before the scan handlers, so these answer PATCH and DELETE on a candidate or session first.
// Like the real API: the commit is one transaction (a failure creates nothing), takes an Idempotency-Key and
// replays the same answer for it, and a viewer cannot change anything (403). Undo removes the items the commit made for good
// (not into Recently deleted) and reopens the session with its candidates, like the real API (SCAN-1e).
import { HttpResponse, http } from 'msw';
import type { CandidateDto, CommitResultDto, ItemDto, PlatformDto, ProvisionalDto, ReleaseDto, UndoResultDto } from '@/api/generated/models';
import { alternativesOf, unresolvedTitleOf } from '@/features/review/grouping';
import type { CommitBody } from '@/features/review/plan';
import { localDateString } from '@/lib/dates';
import { MOCK_LIBRARY_ID, newMockId } from './data';
import { itemsState, type MockItem } from './items';
import { scanMock, type MockSession } from './scan';

export const mockPlatforms: PlatformDto[] = [
  { id: 'e0000000-0000-4000-8000-000000000001', name: 'Nintendo 64', shortName: 'N64', manufacturer: 'Nintendo' },
  { id: 'e0000000-0000-4000-8000-000000000002', name: 'PlayStation 2', shortName: 'PS2', manufacturer: 'Sony' },
];

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

export const reviewMock = {
  /** Set to make the next commit fail as a whole (nothing is created), like a database rollback. */
  failNextCommit: false,
  /** Every :search request the server received. */
  searches: [] as { sessionId: string; candidateId: string; text: string; contextWorkIds: string[] }[],
  /** Every commit request the server received. */
  commits: [] as { sessionId: string; key: string | null; body: CommitBody | null }[],
  /** Every batch-restore request the server received. */
  restores: [] as string[][],
  /** Set to make the next batch restore fail. */
  failNextRestore: false,
  replays: new Map<string, CommitResultDto>(),
  /** Every undo request the server received. */
  undos: [] as { sessionId: string; key: string | null }[],
  /** Set to make the next undo answer with this status (409: too late, or an item changed since). */
  failNextUndo: null as number | null,
  /** The items each session's commit made, so an undo can remove all of them (a candidate remembers only its first copy). */
  made: new Map<string, string[]>(),
  undoReplays: new Map<string, UndoResultDto>(),
  /** Every auto-commit undo request the server received. */
  autoUndos: [] as { sessionId: string; key: string | null; candidateId: string | null }[],
  /** Set to make the next auto-commit undo answer with this status (404: nothing stands; 409: it changed since). */
  failNextAutoUndo: null as number | null,
  autoUndoReplays: new Map<string, unknown>(),
  /** Uploaded segment crops waiting to be claimed by a keep (asset id -> whose library they belong to). */
  crops: new Map<string, string>(),
  /** Every crop upload the server received, so tests can assert successes never upload. */
  cropUploads: [] as { sessionId: string; candidateId: string }[],
  /** Every keep the server received. */
  keeps: [] as { sessionId: string; candidateId: string; cropAssetId: string | null }[],
  promoteReplays: new Map<string, ProvisionalDto>(),
  reset() {
    this.searches = [];
    this.failNextCommit = false;
    this.commits = [];
    this.restores = [];
    this.failNextRestore = false;
    this.replays = new Map();
    this.undos = [];
    this.failNextUndo = null;
    this.made = new Map();
    this.undoReplays = new Map();
    this.autoUndos = [];
    this.failNextAutoUndo = null;
    this.autoUndoReplays = new Map();
    this.crops = new Map();
    this.cropUploads = [];
    this.keeps = [];
    this.promoteReplays = new Map();
  },
};

const keptOf = (session: MockSession): ProvisionalDto[] => (session.provisionals ??= []);

const openProvisionals = (libraryId: string): ProvisionalDto[] => {
  const open: ProvisionalDto[] = [];
  for (const session of scanMock.sessions.values()) {
    if (session.libraryId !== libraryId) continue;
    open.push(...(session.provisionals ?? []).filter((p) => p.resolvedAt === null));
  }
  return open;
};

const findProvisional = (libraryId: string, id: string): { session: MockSession; provisional: ProvisionalDto } | undefined => {
  for (const session of scanMock.sessions.values()) {
    if (session.libraryId !== libraryId) continue;
    const provisional = (session.provisionals ?? []).find((p) => p.id === id);
    if (provisional) return { session, provisional };
  }
  return undefined;
};

const resolve = (c: CandidateDto, release: ReleaseDto) => {
  const owned = Object.values(scanMock.catalog).filter((e) => e.release.id === release.id).flatMap((e) => e.owned);
  Object.assign(c, {
    resolution: 'matched',
    score: 1,
    release,
    alternatives: null,
    alreadyOwnedCount: owned.length,
    ownedAt: owned.map((o) => o.locationPath),
    guidanceCode: null,
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
    acquiredOn: localDateString(),
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
    editionVerified: true,
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
    const body = (await request.json()) as { decision?: CandidateDto['decision'] | null; releaseId?: string | null; copies?: number | null; format?: ReleaseDto['format'] | null; platform?: string | null; edition?: string | null };
    if (body.releaseId) {
      const chosen = alternativesOf(candidate).find((r) => r.id === body.releaseId);
      if (!chosen) return problem(404, 'That is not one of the alternatives.');
      resolve(candidate, chosen);
      candidate.decision = 'accepted';
    }
    if (body.decision) candidate.decision = body.decision;
    if (body.copies) candidate.copies = body.copies;
    if (session.status !== 'open') return problem(409, 'This scan was already finished.');
    if (body.format !== undefined || body.platform !== undefined || body.edition !== undefined) {
      const before = candidate.edits;
      const edits = {
        format: body.format !== undefined ? body.format : (before?.format ?? null),
        platform: body.platform !== undefined ? body.platform : (before?.platform ?? null),
        edition: body.edition !== undefined ? body.edition : (before?.edition ?? null),
      };
      candidate.edits = edits;
      candidate.userEdited = true;
      if (candidate.release) {
        const platform = edits.platform ? (mockPlatforms.find((x) => x.name === edits.platform) ?? null) : candidate.release.platform;
        candidate.release = { ...candidate.release, format: edits.format ?? candidate.release.format, platform, edition: edits.edition ?? candidate.release.edition };
      }
    }
    candidate.seq = ++session.seq;
    candidate.changeSeq = candidate.seq;
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
    const body = (await request.json()) as { text?: string | null; contextWorkIds?: string[] | null };
    const text = (body.text ?? '').trim().toLowerCase();
    if (!text) return problem(400, 'Type a title to search for.');
    reviewMock.searches.push({ sessionId: session.id, candidateId: candidate.id, text, contextWorkIds: body.contextWorkIds ?? [] });
    const found = Object.values(scanMock.catalog).find((e) => e.release.title.toLowerCase().includes(text));
    if (found) resolve(candidate, found.release);
    candidate.seq = ++session.seq;
    return HttpResponse.json(candidate);
  }),

  http.post('*/api/v1/scan-sessions/:id/commit', async ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const key = request.headers.get('Idempotency-Key');
    const text = await request.text();
    const body = text ? (JSON.parse(text) as CommitBody) : null;
    reviewMock.commits.push({ sessionId: session.id, key, body });
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
        const over = body?.overrides?.find((o) => o.candidateId === c.id);
        entry.item.locationId = over?.locationId ?? body?.addTo ?? null;
        entry.item.status = over?.status ?? body?.status ?? 'owned';
        entry.item.condition = over?.condition ?? null;
        itemsState.entries.push(entry);
        itemIds.push(entry.item.id);
        c.createdItemId ??= entry.item.id;
      }
      c.seq = ++session.seq;
    }
    session.status = 'committed';
    reviewMock.made.set(session.id, itemIds);
    const result: CommitResultDto = { created: itemIds.length, skipped, itemIds };
    reviewMock.replays.set(`${session.id}:${key}`, result);
    return HttpResponse.json(result);
  }),

  // MSW can't match the ":undo" suffix as a literal path segment either.
  http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/commit:undo$/, ({ request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const parts = new URL(request.url).pathname.split('/');
    const session = scanMock.sessions.get(String(parts[parts.indexOf('scan-sessions') + 1]));
    if (!session) return problem(404, 'Not found');
    const key = request.headers.get('Idempotency-Key');
    reviewMock.undos.push({ sessionId: session.id, key });
    if (!key) return problem(400, 'An Idempotency-Key header is required.');
    const replay = reviewMock.undoReplays.get(`${session.id}:${key}`);
    if (replay && session.status === 'open') return HttpResponse.json(replay);
    if (reviewMock.failNextUndo) {
      const status = reviewMock.failNextUndo;
      reviewMock.failNextUndo = null;
      return problem(status, status === 409 ? 'It is too late to undo this.' : 'Something went wrong.');
    }
    if (session.status !== 'committed') return problem(409, 'This scan was not added to the library.');

    // Gone for good: no deletedAt, no Recently deleted. The candidates forget the items and are sent again.
    const made = reviewMock.made.get(session.id) ?? [];
    for (const id of made) {
      const at = itemsState.entries.findIndex((e) => e.item.id === id);
      if (at >= 0) itemsState.entries.splice(at, 1);
    }
    reviewMock.made.delete(session.id);
    for (const c of session.candidates.filter((x) => x.createdItemId)) {
      c.createdItemId = null;
      c.seq = ++session.seq;
      c.changeSeq = c.seq;
    }
    for (const stale of [...reviewMock.replays.keys()].filter((k) => k.startsWith(`${session.id}:`))) reviewMock.replays.delete(stale);
    session.status = 'open';
    const result: UndoResultDto = { removed: made.length };
    reviewMock.undoReplays.set(`${session.id}:${key}`, result);
    return HttpResponse.json(result);
  }),

  // MSW can't match the ":undo" suffix as a literal path segment either (and "auto-commit:undo" likewise).
  http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/auto-commit:undo$/, async ({ request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const parts = new URL(request.url).pathname.split('/');
    const session = scanMock.sessions.get(String(parts[parts.indexOf('scan-sessions') + 1]));
    if (!session) return problem(404, 'Not found');
    const key = request.headers.get('Idempotency-Key');
    const text = await request.text();
    const parsed = text ? (JSON.parse(text) as { candidateId?: string | null } | null) : null;
    const candidateId = parsed?.candidateId ?? null;
    reviewMock.autoUndos.push({ sessionId: session.id, key, candidateId });
    if (!key) return problem(400, 'An Idempotency-Key header is required.');
    const replay = reviewMock.autoUndoReplays.get(`${session.id}:${key}`);
    if (replay) return HttpResponse.json(replay);
    if (reviewMock.failNextAutoUndo) {
      const status = reviewMock.failNextAutoUndo;
      reviewMock.failNextAutoUndo = null;
      return problem(status, status === 409 ? 'It changed since, so it stays in your library.' : 'Nothing to undo.');
    }
    if (session.status !== 'open') return problem(409, 'This scan was already finished.');
    const standing = session.candidates.filter((x) => x.createdItemId);
    const target = candidateId ? standing.find((x) => x.id === candidateId) : [...standing].sort((a, b) => b.seq - a.seq)[0];
    if (!target?.createdItemId) return problem(404, 'Nothing to undo.');
    // Gone for good, like a commit undo: the candidate forgets the item and is sent again for Review.
    const removedItemIds = [target.createdItemId];
    for (const id of removedItemIds) {
      const at = itemsState.entries.findIndex((e) => e.item.id === id);
      if (at >= 0) itemsState.entries.splice(at, 1);
    }
    target.createdItemId = null;
    target.seq = ++session.seq;
    target.changeSeq = target.seq;
    const result = { removedItemIds, candidate: target };
    reviewMock.autoUndoReplays.set(`${session.id}:${key}`, result);
    return HttpResponse.json(result);
  }),

  // Batch restore: like the real API, it is all or nothing and leaves live items alone.
  http.post(/\/api\/v1\/libraries\/[^/]+\/items:restore$/, async ({ request }) => {
    const ids = ((await request.json()) as { ids?: string[] | null }).ids ?? [];
    reviewMock.restores.push(ids);
    if (reviewMock.failNextRestore) {
      reviewMock.failNextRestore = false;
      return problem(500, 'Something went wrong.');
    }
    const entries = itemsState.entries.filter((e) => ids.includes(e.item.id) && e.item.deletedAt);
    for (const e of entries) {
      e.item.deletedAt = null;
      e.version += 1;
    }
    return HttpResponse.json({ affected: entries.length });
  }),

  http.get('*/api/v1/catalog/platforms', () => HttpResponse.json({ items: mockPlatforms, nextCursor: null })),

  http.delete('*/api/v1/scan-sessions/:id', ({ params }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    session.status = 'abandoned';
    return new HttpResponse(null, { status: 204 });
  }),

  // Segment crops (#345): kept rows may send theirs, successes never do. Like the real API: 1 MB max, sanitized ScanPhoto.
  http.post('*/api/v1/scan-sessions/:id/candidates/:cid/crop', async ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    const candidate = session?.candidates.find((c) => c.id === params.cid);
    if (!session || !candidate) return problem(404, 'Not found');
    if (session.status !== 'open') return problem(409, 'This scan was already finished.');
    if (candidate.decision === 'accepted' || candidate.createdItemId) return problem(409, 'That item is already on its way into the library.');
    const form = (await request.formData().catch(() => null)) as unknown as { get(name: string): unknown } | null;
    const file = form?.get('file');
    if (!(file instanceof Blob) || file.size === 0) return problem(400, 'Send the crop as a multipart part named "file".');
    if (file.size > 1024 * 1024) return problem(413, 'That crop is over 1 MB.');
    const assetId = newMockId();
    reviewMock.crops.set(assetId, session.libraryId);
    reviewMock.cropUploads.push({ sessionId: session.id, candidateId: candidate.id });
    return HttpResponse.json({ assetId, cropUrl: `/media/covers/${assetId}` });
  }),

  // "Keep as unverified" (#345): the T2 row retires and its provisional record is born.
  http.post('*/api/v1/scan-sessions/:id/provisionals', async ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    const body = (await request.json()) as { candidateId?: string | null; cropAssetId?: string | null };
    const candidate = session?.candidates.find((c) => c.id === body.candidateId);
    if (!session || !candidate) return problem(404, 'Not found');
    if (session.status !== 'open') return problem(409, 'This scan was already finished.');
    if (candidate.decision === 'accepted' || candidate.createdItemId) return problem(409, 'That item is already on its way into the library.');
    const kept = keptOf(session);
    if (kept.some((p) => p.candidateId === candidate.id && p.resolvedAt === null)) return problem(409, 'That row was already kept as unverified.');
    let cropUrl: string | null = null;
    if (body.cropAssetId) {
      if (reviewMock.crops.get(body.cropAssetId) !== session.libraryId) return problem(400, "That crop doesn't exist in this library.");
      if (kept.some((p) => p.cropUrl === `/media/covers/${body.cropAssetId}`)) return problem(409, 'That crop already belongs to another kept row.');
      cropUrl = `/media/covers/${body.cropAssetId}`;
    }
    reviewMock.keeps.push({ sessionId: session.id, candidateId: candidate.id, cropAssetId: body.cropAssetId ?? null });
    const provisional: ProvisionalDto = {
      id: newMockId(),
      libraryId: session.libraryId,
      sessionId: session.id,
      snapId: null,
      titleGuess: unresolvedTitleOf(candidate) || candidate.visibleText?.trim() || (candidate.rawCode ? `Code ${candidate.rawCode}` : 'Unknown item'),
      isGuess: true,
      mediaHint: session.context?.mediaHint ?? 'mixed',
      locationId: session.context?.defaultLocationId ?? null,
      status: 'unverified',
      candidateId: candidate.id,
      cropUrl,
      createdAt: new Date().toISOString(),
      resolvedAt: null,
      resolveAction: null,
      createdItemId: null,
    };
    kept.push(provisional);
    Object.assign(candidate, { decision: 'rejected', guidanceCode: null, cropUrl, seq: ++session.seq, changeSeq: session.seq } satisfies Partial<CandidateDto>);
    return HttpResponse.json({ provisional, candidate }, { status: 201 });
  }),

  // "Needs attention" (#345): the library's open provisionals, stalest first.
  http.get('*/api/v1/libraries/:libraryId/provisionals', ({ params }) => {
    if (String(params.libraryId) !== MOCK_LIBRARY_ID) return problem(404, 'Not found');
    return HttpResponse.json({ items: openProvisionals(MOCK_LIBRARY_ID) });
  }),

  // Promote (#345): the guess becomes a real item, the crop its cover; the same key replays the same answer.
  http.post(/\/api\/v1\/libraries\/([^/]+)\/provisionals\/([^/]+):promote$/, async ({ request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const parts = new URL(request.url).pathname.split('/');
    const libraryId = String(parts[parts.indexOf('libraries') + 1]);
    const id = String(parts[parts.length - 1]?.replace(':promote', ''));
    const found = findProvisional(libraryId, id);
    if (!found) return problem(404, 'Not found');
    const key = request.headers.get('Idempotency-Key');
    if (!key) return problem(400, 'An Idempotency-Key header is required.');
    const replay = reviewMock.promoteReplays.get(`${id}:${key}`);
    if (replay) return HttpResponse.json(replay);
    const { session, provisional } = found;
    if (provisional.resolvedAt !== null) return problem(409, 'That record was already acted on.');
    const release: ReleaseDto = {
      id: newMockId(), workId: newMockId(), title: provisional.titleGuess, format: 'other', platform: null, region: null,
      edition: null, publisher: null, releaseDate: null, year: null, creators: [], pageCount: null, runtimeMinutes: null,
      coverUrl: null, source: 'user', identifiers: [],
    };
    const entry = makeItem(release);
    entry.item.locationId = provisional.locationId;
    if (provisional.cropUrl) {
      entry.item.coverChoice = 'my_photo';
      entry.item.customCoverAssetId = provisional.cropUrl.split('/').pop() ?? null;
    }
    itemsState.entries.push(entry);
    const candidate = session.candidates.find((c) => c.id === provisional.candidateId);
    if (candidate) candidate.createdItemId = entry.item.id;
    Object.assign(provisional, { resolvedAt: new Date().toISOString(), resolveAction: 'promoted', createdItemId: entry.item.id });
    reviewMock.promoteReplays.set(`${id}:${key}`, provisional);
    return HttpResponse.json(provisional);
  }),

  // Resolve to existing (#345): the row is that item; the crop is dropped.
  http.post(/\/api\/v1\/libraries\/([^/]+)\/provisionals\/([^/]+):resolve$/, async ({ request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const parts = new URL(request.url).pathname.split('/');
    const libraryId = String(parts[parts.indexOf('libraries') + 1]);
    const id = String(parts[parts.length - 1]?.replace(':resolve', ''));
    const found = findProvisional(libraryId, id);
    if (!found) return problem(404, 'Not found');
    const body = (await request.json()) as { itemId?: string | null };
    const entry = itemsState.entries.find((e) => e.item.id === body.itemId && !e.item.deletedAt);
    if (!entry) return problem(404, "That item doesn't exist.");
    const { session, provisional } = found;
    if (provisional.resolvedAt !== null) return problem(409, 'That record was already acted on.');
    const candidate = session.candidates.find((c) => c.id === provisional.candidateId);
    if (candidate) {
      resolve(candidate, entry.release);
      candidate.decision = 'rejected';
    }
    Object.assign(provisional, { cropUrl: null, resolvedAt: new Date().toISOString(), resolveAction: 'resolved_to_existing', createdItemId: entry.item.id });
    return HttpResponse.json(provisional);
  }),

  http.delete('*/api/v1/libraries/:libraryId/provisionals/:id', ({ params }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const found = findProvisional(String(params.libraryId), String(params.id));
    if (!found) return problem(404, 'Not found');
    found.session.provisionals = (found.session.provisionals ?? []).filter((p) => p.id !== found.provisional.id);
    return new HttpResponse(null, { status: 204 });
  }),
];
