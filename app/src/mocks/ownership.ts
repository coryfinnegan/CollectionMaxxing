// Mock of GET /libraries/{id}/ownership/snapshot: pages, a cursor, and tombstones for deleted items.
// The cursor is the sequence number of the last change handed out. Tests change the mock library with the helpers below.
// Every live item is in the snapshot with its status; only owned, lent and missing are the library.
// GET /libraries/{id}/ownership answers from the same entries, like the real OwnershipQuery: exact copies in the library, plus
// same-work other-shape soft matches, plus the wishlist item and the most recent sold copy of the release asked about.
import { HttpResponse, http } from 'msw';
import type { ItemStatus, OwnedCopyDto, OwnershipSnapshotEntryDto } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from './data';

type Change = { seq: number; entry: OwnershipSnapshotEntryDto };

export const ownershipMock = {
  seq: 0,
  changes: [] as Change[],
  /** Set to make the next snapshot request fail with this status (then clears). */
  failNext: null as number | null,
};

/** The mock catalog: which work a release or a barcode names, so soft matches work like the server's. */
const releaseWorks = new Map<string, string>();
const barcodeWorks = new Map<string, string>();

const learnWork = (entry: OwnershipSnapshotEntryDto) => {
  if (entry.deleted || !entry.workId) return;
  if (entry.releaseId) releaseWorks.set(entry.releaseId, entry.workId);
  for (const identifier of entry.identifiers) barcodeWorks.set(identifier, entry.workId);
};

const id = (n: number) => `${String(n).padStart(8, "0")}-0000-4000-8000-000000000000`;

const owned = (n: number, title: string, format: OwnershipSnapshotEntryDto['format'], identifiers: string[], locationPath: string | null, workN = n): OwnershipSnapshotEntryDto => ({
  itemId: id(n),
  deleted: false,
  releaseId: id(n + 100),
  workId: id(workN + 200),
  title,
  format,
  platform: null,
  identifiers,
  locationPath,
  status: 'owned',
});

/** The server's ItemStatuses.InCollection: what counts as owned. */
const inTheLibrary = (status: ItemStatus | null | undefined): boolean => status === undefined || status === null || status === 'owned' || status === 'lent' || status === 'missing';

/** Adds or replaces an item. */
export function upsertOwned(entry: OwnershipSnapshotEntryDto) {
  ownershipMock.changes.push({ seq: ++ownershipMock.seq, entry });
  learnWork(entry);
}

/** Marks an item deleted (a tombstone). */
export function deleteOwned(itemId: string) {
  upsertOwned({
    itemId,
    deleted: true,
    releaseId: null,
    workId: null,
    title: null,
    format: null,
    platform: null,
    identifiers: [],
    locationPath: null,
    status: null,
  });
}

export function resetOwnershipMock() {
  ownershipMock.seq = 0;
  ownershipMock.changes = [];
  ownershipMock.failNext = null;
  releaseWorks.clear();
  barcodeWorks.clear();
  upsertOwned(owned(1, 'Jaws', 'vhs', ['0042100005264'], 'Living room › Shelf B'));
  upsertOwned(owned(2, 'Jaws', 'dvd', ['5050582000000'], 'Living room › Shelf A', 1));
  upsertOwned(owned(3, 'Dune', 'paperback', ['9780306406157'], 'Office › Shelf 1'));
}
resetOwnershipMock();

const digits = (value: string) => value.replace(/\D/g, '');

const latestEntries = (): OwnershipSnapshotEntryDto[] => {
  const latest = new Map<string, Change>();
  for (const change of ownershipMock.changes) latest.set(change.entry.itemId, change);
  return [...latest.values()].map((c) => c.entry).filter((e) => !e.deleted);
};

const toCopy = (e: OwnershipSnapshotEntryDto): OwnedCopyDto => ({
  itemId: e.itemId,
  releaseId: e.releaseId ?? '',
  workId: e.workId ?? '',
  title: e.title ?? 'Unknown item',
  format: e.format ?? 'other',
  platform: e.platform,
  status: e.status ?? 'owned',
  locationId: null,
  locationPath: e.locationPath,
});

export const handlers = [
  http.get('*/api/v1/libraries/:libraryId/ownership', ({ params, request }) => {
    const url = new URL(request.url);
    const code = url.searchParams.get('code');
    const releaseId = url.searchParams.get('releaseId');
    const workId = url.searchParams.get('workId');
    if (params.libraryId !== MOCK_LIBRARY_ID) {
      return HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404, headers: { 'Content-Type': 'application/problem+json' } });
    }
    const everything = latestEntries().filter((e) => e.releaseId && e.workId && e.format);
    const asked = everything.filter(
      (e) =>
        (releaseId && e.releaseId === releaseId) ||
        (code && e.identifiers.some((i) => i === code || (digits(i) !== '' && digits(i) === digits(code)))) ||
        (workId && !releaseId && !code && e.workId === workId),
    );
    // Only what you have is owned or a soft match; the wishlist and sold items are named on their own.
    const entries = everything.filter((e) => inTheLibrary(e.status));
    const exact = asked.filter((e) => inTheLibrary(e.status));
    const byRelease = !!releaseId || !!code;
    const wishlisted = byRelease ? asked.filter((e) => e.status === 'wishlist').map((e) => e.itemId).sort()[0] : undefined;
    const sold = byRelease ? asked.filter((e) => e.status === 'sold').map((e) => e.itemId).sort().at(-1) : undefined;
    // Soft matches: other copies of the same work in another shape. The work comes from the catalog mapping
    // when nothing is owned exactly (an unowned release of an owned work), like the server's ForReleasesAsync.
    const works = new Set(asked.map((e) => e.workId));
    if (workId) works.add(workId);
    if (releaseId && releaseWorks.has(releaseId)) works.add(releaseWorks.get(releaseId)!);
    if (code && barcodeWorks.has(code)) works.add(barcodeWorks.get(code)!);
    const shapes = new Set(exact.map((e) => `${e.format}::${e.platform ?? ''}`));
    const soft = entries.filter(
      (e) => works.has(e.workId) && !exact.some((x) => x.itemId === e.itemId) && !shapes.has(`${e.format}::${e.platform ?? ''}`),
    );
    return HttpResponse.json({
      owned: exact.length > 0,
      count: exact.length,
      copies: exact.map(toCopy),
      sameWorkOtherFormat: soft.map(toCopy),
      wishlistItemId: wishlisted ?? null,
      soldItemId: sold ?? null,
    });
  }),
  http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ params, request }) => {
    if (params.libraryId !== MOCK_LIBRARY_ID) {
      return HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404, headers: { 'Content-Type': 'application/problem+json' } });
    }
    if (ownershipMock.failNext) {
      const status = ownershipMock.failNext;
      ownershipMock.failNext = null;
      return HttpResponse.json({ title: 'Server error', status }, { status, headers: { 'Content-Type': 'application/problem+json' } });
    }
    const url = new URL(request.url);
    const since = url.searchParams.get('since');
    const limit = Math.min(Number(url.searchParams.get('limit') ?? 200) || 200, 500);
    const after = since ? Number(since) : 0;
    if (since && !Number.isFinite(after)) {
      return HttpResponse.json({ title: "That cursor isn't valid.", status: 400 }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } });
    }

    // From scratch: every live item now, with its status. With a cursor: every change since, tombstones included.
    const latest = new Map<string, Change>();
    for (const change of ownershipMock.changes) latest.set(change.entry.itemId, change);
    const pending = [...latest.values()]
      .filter((c) => c.seq > after && (since ? true : !c.entry.deleted))
      .sort((a, b) => a.seq - b.seq);
    const page = pending.slice(0, limit);
    const hasMore = pending.length > limit;
    const nextCursor = String(page.length > 0 ? page[page.length - 1].seq : after);
    return HttpResponse.json({ entries: page.map((c) => c.entry), nextCursor, hasMore });
  }),
];
