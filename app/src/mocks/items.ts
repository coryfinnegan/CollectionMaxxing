// Mock Items endpoints. They behave like the real API: ETag on GET, If-Match required on PATCH and DELETE
// (412 on mismatch, 428 when missing), Idempotency-Key on create, cursor pagination, soft delete with restore.
import { HttpResponse, http } from 'msw';
import type {
  CreateItemRequest,
  ItemDetailDto,
  ItemDto,
  ItemStatus,
  ItemSummaryDto,
  ReleaseDto,
  ReleaseFormat,
  UpdateItemRequest,
  WorkDto,
  WorkKind,
} from '@/api/generated/models';
import { catalogReleases } from './catalog';
import { newMockId } from './data';

export type MockItem = { item: ItemDto; work: WorkDto; release: ReleaseDto; version: number };

const GAME_ROOM = 'a0000000-0000-4000-8000-000000000001';
const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';
const SHELF_2 = 'a0000000-0000-4000-8000-000000000003';
const LIVING_ROOM = 'a0000000-0000-4000-8000-000000000004';
const MEMBER = '22222222-2222-4222-8222-222222222222';
const CREATED = '2026-09-01T12:00:00Z';

type Row = [title: string, kind: WorkKind, format: ReleaseFormat, platform: string | null, year: number, status: ItemStatus, locationId: string | null];

const rows: Row[] = [
  ['The Matrix', 'film', 'dvd', null, 1999, 'owned', LIVING_ROOM],
  ['Back to the Future', 'film', 'vhs', null, 1985, 'owned', LIVING_ROOM],
  ['Dune', 'book', 'paperback', null, 1965, 'owned', SHELF_2],
  ['Super Mario 64', 'game', 'game_cartridge', 'Nintendo 64', 1996, 'owned', SHELF_1],
  ['GoldenEye 007', 'game', 'game_cartridge', 'Nintendo 64', 1997, 'lent', SHELF_1],
  ['Blade Runner', 'film', 'bluray', null, 1982, 'wishlist', null],
  ['Hades', 'game', 'game_disc', 'PlayStation 5', 2020, 'wishlist', null],
];

const makeEntry = (n: number, minutesAfterStart: number, [title, kind, format, platform, year, status, locationId]: Row): MockItem => {
  const id = `c0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  const workId = `d0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  const releaseId = `e0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  const cover = null;
  const item: ItemDto = {
    id,
    releaseId,
    status,
    condition: n % 2 === 0 ? 'good' : null,
    completeness: null,
    locationId,
    notes: null,
    acquiredOn: null,
    lentTo: status === 'lent' ? 'Sam' : null,
    lentOn: status === 'lent' ? '2026-09-20' : null,
    customTitle: null,
    customCoverAssetId: null,
    coverChoice: 'release',
    addedVia: 'manual',
    tagIds: [],
    createdAt: new Date(Date.parse(CREATED) + minutesAfterStart * 60_000).toISOString(),
    createdBy: MEMBER,
    updatedAt: CREATED,
    deletedAt: null,
    editionVerified: true,
  };
  const work: WorkDto = { id: workId, kind, title, originalYear: year, creators: [], genres: [], synopsis: null, coverUrl: cover };
  const release: ReleaseDto = {
    id: releaseId,
    workId,
    title,
    format,
    platform: platform ? { id: `p-${platform}`, name: platform, shortName: platform, manufacturer: null } : null,
    region: null,
    edition: null,
    publisher: null,
    releaseDate: null,
    year,
    creators: [],
    pageCount: null,
    runtimeMinutes: null,
    coverUrl: cover,
    source: 'user',
    identifiers: [],
  };
  return { item, work, release, version: 1 };
};

/** Fresh seed: the hand-written rows plus enough owned filler to need a second page. */
export const seed = (): MockItem[] => {
  const base = rows.map((row, i) => makeEntry(i + 1, rows.length - i, row));
  const filler = Array.from({ length: 28 }, (_, i) =>
    makeEntry(rows.length + i + 1, -(i + 1), [`Shelf filler ${String(i + 1).padStart(2, '0')}`, 'book', 'paperback', null, 1990 + i, 'owned', GAME_ROOM]),
  );
  return [...base, ...filler];
};

export const itemsState: { entries: MockItem[]; replays: Map<string, { body: string; response: ItemDto }> } = {
  entries: seed(),
  replays: new Map(),
};

export const resetItems = (): void => {
  itemsState.entries = seed();
  itemsState.replays = new Map();
};

const problem = (status: number, title: string, errors?: Record<string, string[]>, extra?: Record<string, string>) =>
  HttpResponse.json(
    { title, status, ...(errors ? { errors } : {}), ...extra },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );

/** A barcode of another work: the mock refuses it naming Dune Messiah, like the real API names the owning title. */
export const CROSS_WORK_BARCODE = '9999999999994';

const etagOf = (entry: MockItem) => `"v${entry.version}"`;
const find = (id: unknown) => itemsState.entries.find((e) => e.item.id === id);
const live = (id: unknown) => {
  const entry = find(id);
  return entry && !entry.item.deletedAt ? entry : undefined;
};

const summary = ({ item, work, release }: MockItem): ItemSummaryDto => ({
  id: item.id,
  releaseId: release.id,
  workId: work.id,
  title: item.customTitle ?? work.title,
  kind: work.kind,
  format: release.format,
  platform: release.platform?.name ?? null,
  year: release.year,
  status: item.status,
  condition: item.condition,
  locationId: item.locationId,
  coverUrl: release.coverUrl,
  createdAt: item.createdAt,
  editionVerified: item.editionVerified,
});

const detail = (entry: MockItem): ItemDetailDto => ({
  item: entry.item,
  work: entry.work,
  release: entry.release,
  otherCopies: itemsState.entries
    .filter((e) => !e.item.deletedAt && e.work.id === entry.work.id && e.item.id !== entry.item.id)
    .map((e) => ({
      itemId: e.item.id,
      releaseId: e.release.id,
      format: e.release.format,
      platform: e.release.platform?.name ?? null,
      status: e.item.status,
      locationId: e.item.locationId,
    })),
});

const sorters: Record<string, (a: ItemSummaryDto, b: ItemSummaryDto) => number> = {
  title: (a, b) => a.title.localeCompare(b.title),
  year: (a, b) => (a.year ?? 0) - (b.year ?? 0),
  platform: (a, b) => (a.platform ?? '').localeCompare(b.platform ?? '') || a.title.localeCompare(b.title),
  '-addedAt': (a, b) => b.createdAt.localeCompare(a.createdAt),
};

const base = '*/api/v1/libraries/:libraryId/items';

export const handlers = [
  http.get(base, ({ request }) => {
    const url = new URL(request.url);
    const q = (name: string) => url.searchParams.get(name) ?? undefined;
    const text = q('q')?.toLowerCase();
    let list = itemsState.entries.filter((e) => !e.item.deletedAt).map(summary);
    if (text) list = list.filter((i) => i.title.toLowerCase().includes(text));
    if (q('kind')) list = list.filter((i) => i.kind === q('kind'));
    if (q('format')) list = list.filter((i) => i.format === q('format'));
    if (q('platform')) list = list.filter((i) => i.platform?.toLowerCase() === q('platform')?.toLowerCase());
    if (q('status')) list = list.filter((i) => i.status === q('status'));
    if (q('locationId')) list = list.filter((i) => i.locationId === q('locationId'));
    if (q('editionVerified') !== undefined) list = list.filter((i) => i.editionVerified === (q('editionVerified') === 'true'));
    list.sort(sorters[q('sort') ?? '-addedAt'] ?? sorters['-addedAt']);

    const limit = Math.min(Number(q('limit') ?? 50) || 50, 200);
    const start = q('cursor') ? Number.parseInt(q('cursor') as string, 10) : 0;
    if (Number.isNaN(start) || start < 0) return problem(400, 'The cursor is not valid.');
    const page = list.slice(start, start + limit);
    const next = start + limit < list.length ? String(start + limit) : null;
    return HttpResponse.json({ items: page, nextCursor: next });
  }),

  http.get(`${base}/:id`, ({ params }) => {
    const entry = live(params.id);
    if (!entry) return problem(404, 'Not found');
    return HttpResponse.json(detail(entry), { headers: { ETag: etagOf(entry) } });
  }),

  http.post(base, async ({ request }) => {
    const key = request.headers.get('Idempotency-Key');
    if (!key) return problem(400, 'An Idempotency-Key header is required.');
    const raw = await request.text();
    const replay = itemsState.replays.get(key);
    if (replay) {
      if (replay.body !== raw) return problem(422, 'That Idempotency-Key was already used for a different request.');
      return HttpResponse.json(replay.response, { status: 201 });
    }
    const body = JSON.parse(raw) as CreateItemRequest;
    const n = itemsState.entries.length + 1000;
    let entry: MockItem;
    if (body.releaseId) {
      const found = catalogReleases().find((c) => c.release.id === body.releaseId);
      if (!found) return problem(404, 'That release is not in the catalog.');
      entry = makeEntry(n, 0, [found.work.title, found.work.kind, found.release.format, found.release.platform?.name ?? null, found.release.year ?? 0, 'owned', null]);
      entry.work.id = found.work.id;
      entry.release = { ...found.release };
    } else {
      const title = body.manual?.title?.trim();
      if (!title) return problem(400, 'One or more validation errors occurred.', { title: ['Title is required.'] });
      entry = makeEntry(n, 0, [title, body.manual?.kind ?? 'other', body.manual?.format ?? 'other', null, body.manual?.year ?? 0, 'owned', null]);
      if (!body.manual?.year) {
        entry.work.originalYear = null;
        entry.release.year = null;
      }
    }
    entry.item.id = newMockId();
    entry.item.createdAt = new Date().toISOString();
    entry.item.addedVia = body.addedVia ?? (body.releaseId ? 'search' : 'manual');
    if (body.status) entry.item.status = body.status;
    if (body.locationId !== undefined) entry.item.locationId = body.locationId;
    if (body.notes !== undefined) entry.item.notes = body.notes;
    itemsState.entries.push(entry);
    itemsState.replays.set(key, { body: raw, response: entry.item });
    return HttpResponse.json(entry.item, { status: 201, headers: { ETag: etagOf(entry) } });
  }),

  http.patch(`${base}/:id`, async ({ params, request }) => {
    const entry = live(params.id);
    if (!entry) return problem(404, 'Not found');
    const ifMatch = request.headers.get('If-Match');
    if (!ifMatch) return problem(428, 'An If-Match header is required.');
    if (ifMatch !== etagOf(entry)) return problem(412, 'This item changed since you loaded it.');
    const body = (await request.json()) as UpdateItemRequest;
    Object.assign(entry.item, Object.fromEntries(Object.entries(body).filter(([, v]) => v !== undefined)));
    entry.item.updatedAt = new Date().toISOString();
    entry.version += 1;
    return HttpResponse.json(entry.item, { headers: { ETag: etagOf(entry) } });
  }),

  http.delete(`${base}/:id`, ({ params, request }) => {
    const entry = live(params.id);
    if (!entry) return problem(404, 'Not found');
    const ifMatch = request.headers.get('If-Match');
    if (!ifMatch) return problem(428, 'An If-Match header is required.');
    if (ifMatch !== etagOf(entry)) return problem(412, 'This item changed since you loaded it.');
    entry.item.deletedAt = new Date().toISOString();
    entry.version += 1;
    return new HttpResponse(null, { status: 204 });
  }),

  // MSW can't match the ":restore" suffix as a literal path segment, so match on the whole tail.
  http.post(/\/api\/v1\/libraries\/[^/]+\/items\/([^/]+):restore$/, ({ request }) => {
    const id = new URL(request.url).pathname.split('/').pop()?.replace(':restore', '');
    const entry = find(id);
    if (!entry) return problem(404, 'Not found');
    entry.item.deletedAt = null;
    entry.version += 1;
    return HttpResponse.json(entry.item, { headers: { ETag: etagOf(entry) } });
  }),

  // :rematch (#346): a release swaps the link; a barcode verifies the edition in place (the mock's rows each
  // have their own work, so there is no second edition to swap to — the server tests prove the swap). The
  // cross-work barcode is refused naming its owner, like the real API.
  http.post(/\/api\/v1\/libraries\/[^/]+\/items\/([^/]+):rematch$/, async ({ request }) => {
    const id = new URL(request.url).pathname.split('/').pop()?.replace(':rematch', '');
    const entry = live(id);
    if (!entry) return problem(404, 'Not found');
    const body = (await request.json()) as { releaseId?: string | null; barcode?: string | null };
    if (body.releaseId && body.barcode) return problem(400, 'Send a release or a barcode, not both.');
    if (body.barcode === CROSS_WORK_BARCODE) {
      return problem(409, 'Conflict', undefined, { detail: 'That barcode belongs to Dune Messiah.', code: 'item.barcode_other_work' });
    }
    if (body.barcode) {
      entry.item.editionVerified = true;
      entry.item.updatedAt = new Date().toISOString();
      entry.version += 1;
      return HttpResponse.json(entry.item, { headers: { ETag: etagOf(entry) } });
    }
    if (body.releaseId) {
      entry.item.releaseId = body.releaseId;
      entry.item.updatedAt = new Date().toISOString();
      entry.version += 1;
      return HttpResponse.json(entry.item, { headers: { ETag: etagOf(entry) } });
    }
    return problem(400, 'Say which release to switch to.');
  }),
];
