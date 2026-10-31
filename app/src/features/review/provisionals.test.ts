// "Keep as unverified" (#345): a T2 row's crop goes up only when the row is kept, never for successes,
// and a keep that cannot reach the server waits in the outbox instead of losing the row.
import type { CandidateDto, CreateProvisionalResultDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { ApiError } from '@/lib/http';
import { candidate } from './testData';
import {
  createProvisionalOutbox,
  findDroppedSegments,
  flushProvisionalOutbox,
  getProvisionalOutbox,
  keepAsUnverified,
  MAX_CROP_BYTES,
  setProvisionalCropReader,
  setProvisionalOutbox,
  setProvisionalTransport,
  type ProvisionalTransport,
} from './provisionals';

const kept = (c: CandidateDto): CreateProvisionalResultDto =>
  ({
    provisional: {
      id: 'p1',
      libraryId: 'lib',
      sessionId: 'session',
      snapId: null,
      titleGuess: 'Zzxq',
      isGuess: true,
      mediaHint: 'mixed',
      locationId: null,
      status: 'unverified',
      candidateId: c.id,
      cropUrl: null,
      createdAt: '2026-10-26T00:00:00Z',
      resolvedAt: null,
      resolveAction: null,
      createdItemId: null,
    },
    candidate: { ...c, decision: 'rejected' },
  }) as CreateProvisionalResultDto;

/** The test proxy: records every crop upload and keep, so the tests prove what was (not) sent. */
const recording = (fail?: { uploads?: Error; keeps?: Error }) => {
  const calls = { uploads: [] as { sessionId: string; candidateId: string; size: number }[], keeps: [] as { sessionId: string; candidateId: string; cropAssetId?: string }[] };
  const transport: ProvisionalTransport = {
    async uploadCrop(sessionId, candidateId, file) {
      calls.uploads.push({ sessionId, candidateId, size: file.size });
      if (fail?.uploads) throw fail.uploads;
      return { assetId: 'asset-1', cropUrl: '/media/covers/asset-1' };
    },
    async keep(sessionId, candidateId, cropAssetId) {
      calls.keeps.push({ sessionId, candidateId, cropAssetId });
      if (fail?.keeps) throw fail.keeps;
      return kept(candidate({ id: candidateId }));
    },
  };
  return { calls, transport };
};

const blobOf = (size: number) => ({ size }) as Blob;

beforeEach(() => {
  useCardsStore.getState().reset();
  setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
  setProvisionalCropReader(async () => blobOf(100));
});

afterEach(() => {
  setProvisionalTransport(undefined);
  setProvisionalCropReader(undefined);
  setProvisionalOutbox(undefined);
});

describe('keepAsUnverified', () => {
  it('uploads the crop, keeps the row, and merges the retired row', async () => {
    const proxy = recording();
    setProvisionalTransport(proxy.transport);
    const row = candidate({ resolution: 'not_found', release: null });

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: row, cropUri: 'file:///crop.jpg' });

    expect(result).toEqual({ ok: true });
    expect(proxy.calls.uploads).toHaveLength(1);
    expect(proxy.calls.keeps).toEqual([{ sessionId: 'session', candidateId: row.id, cropAssetId: 'asset-1' }]);
    expect(useCardsStore.getState().byId[row.id]?.decision).toBe('rejected');
  });

  it('keeps without uploading when the phone has no crop', async () => {
    const proxy = recording();
    setProvisionalTransport(proxy.transport);
    const row = candidate({ resolution: 'ambiguous' });

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: row, cropUri: undefined });

    expect(result).toEqual({ ok: true });
    expect(proxy.calls.uploads).toHaveLength(0);
    expect(proxy.calls.keeps).toEqual([{ sessionId: 'session', candidateId: row.id, cropAssetId: undefined }]);
  });

  it.each([
    ['an accepted row', { decision: 'accepted' } as Partial<CandidateDto>],
    ['an added row', { createdItemId: 'item-1' } as Partial<CandidateDto>],
  ])('never uploads for a success: %s is blocked with zero calls', async (_name, extra) => {
    const proxy = recording();
    setProvisionalTransport(proxy.transport);

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: candidate(extra), cropUri: 'file:///crop.jpg' });

    expect(result.ok).toBe(false);
    expect(proxy.calls.uploads).toHaveLength(0);
    expect(proxy.calls.keeps).toHaveLength(0);
  });

  it('blocks a viewer with zero calls', async () => {
    const proxy = recording();
    setProvisionalTransport(proxy.transport);

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: false, sessionId: 'session', candidate: candidate({ resolution: 'not_found' }), cropUri: 'file:///crop.jpg' });

    expect(result).toEqual({ ok: false, reason: 'blocked' });
    expect(proxy.calls.uploads).toHaveLength(0);
    expect(proxy.calls.keeps).toHaveLength(0);
  });

  it('refuses an oversize crop before anything is sent', async () => {
    const proxy = recording();
    setProvisionalTransport(proxy.transport);
    setProvisionalCropReader(async () => blobOf(MAX_CROP_BYTES + 1));

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: candidate({ resolution: 'not_found' }), cropUri: 'file:///huge.jpg' });

    expect(result.ok).toBe(false);
    expect(proxy.calls.uploads).toHaveLength(0);
    expect(proxy.calls.keeps).toHaveLength(0);
  });

  it('queues the keep when the server cannot be reached', async () => {
    const offline = recording({ keeps: new TypeError('fetch failed') });
    setProvisionalTransport(offline.transport);
    const row = candidate({ resolution: 'not_found' });

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: row, cropUri: undefined });

    expect(result).toEqual({ ok: true, queued: true });
    expect(useCardsStore.getState().byId[row.id]).toBeUndefined(); // the row stays a visible T2 row until the keep lands
  });

  it('queues the keep when the server says to try again later (429), instead of failing the row', async () => {
    setProvisionalTransport(recording({ keeps: new ApiError(429, {}) }).transport);
    const row = candidate({ resolution: 'not_found' });

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: row, cropUri: undefined });

    expect(result).toEqual({ ok: true, queued: true });
    expect(getProvisionalOutbox().list('lib').map((e) => e.candidateId)).toEqual([row.id]);
  });

  it('treats an already-kept conflict as stale, so the row reloads instead of duplicating', async () => {
    const proxy = recording({ keeps: new ApiError(409, {}) });
    setProvisionalTransport(proxy.transport);

    const result = await keepAsUnverified({ libraryId: 'lib', canEdit: true, sessionId: 'session', candidate: candidate({ resolution: 'not_found' }), cropUri: undefined });

    expect(result).toEqual({ ok: false, reason: 'stale' });
  });
});

describe('flushProvisionalOutbox', () => {
  const queue = (candidateId: string, cropUri?: string) =>
    getProvisionalOutbox().enqueue({ libraryId: 'lib', sessionId: 'session', candidateId, ...(cropUri ? { cropUri } : {}) });

  it.each([401, 408, 429])('keeps a queued keep the server answers with %i (try again later) and stops', async (status) => {
    const proxy = recording({ keeps: new ApiError(status, {}) });
    setProvisionalTransport(proxy.transport);
    queue('row-1');
    queue('row-2');

    await expect(flushProvisionalOutbox({ libraryId: 'lib' })).resolves.toBe(2);

    expect(proxy.calls.keeps).toHaveLength(1); // it stopped at the first
    expect(getProvisionalOutbox().list('lib')).toHaveLength(2);
  });

  it('keeps a queued keep whose crop upload is told to slow down (429), instead of losing the row', async () => {
    const proxy = recording({ uploads: new ApiError(429, {}) });
    setProvisionalTransport(proxy.transport);
    queue('row-1', 'file:///crop.jpg');

    await expect(flushProvisionalOutbox({ libraryId: 'lib' })).resolves.toBe(1);

    expect(proxy.calls.keeps).toHaveLength(0);
  });

  it.each([403, 404])('drops a queued keep the server refuses for good (%i) and still sends the rest', async (status) => {
    const proxy = recording({ keeps: new ApiError(status, {}) });
    const first = proxy.transport.keep;
    let calls = 0;
    setProvisionalTransport({
      ...proxy.transport,
      keep: (sessionId, candidateId, cropAssetId) => (++calls === 1 ? first(sessionId, candidateId, cropAssetId) : Promise.resolve(kept(candidate({ id: candidateId })))),
    });
    queue('row-1');
    queue('row-2');

    await expect(flushProvisionalOutbox({ libraryId: 'lib' })).resolves.toBe(0);

    expect(getProvisionalOutbox().list('lib')).toHaveLength(0);
  });

  it('drops a keep that landed before (409) and tells the caller to reload', async () => {
    setProvisionalTransport(recording({ keeps: new ApiError(409, {}) }).transport);
    queue('row-1');
    const onStale = jest.fn();

    await expect(flushProvisionalOutbox({ libraryId: 'lib', onStale })).resolves.toBe(0);

    expect(onStale).toHaveBeenCalledTimes(1);
    expect(getProvisionalOutbox().list('lib')).toHaveLength(0);
  });
});

describe('findDroppedSegments', () => {
  const snap = (items: { localId: string; candidateId?: string }[]) => ({ snapId: 'snap-1', items }) as never;

  it('is empty when every sent item has a visible row or an open provisional', () => {
    const dropped = findDroppedSegments(
      [snap([{ localId: 'a', candidateId: 'c1' }, { localId: 'b', candidateId: 'c2' }])],
      [candidate({ id: 'c1' })],
      [{ candidateId: 'c2', resolvedAt: null } as never],
      new Set(),
    );
    expect(dropped).toEqual([]);
  });

  it('names a sent item with neither a row nor a provisional', () => {
    const dropped = findDroppedSegments([snap([{ localId: 'a', candidateId: 'c-gone' }])], [], [], new Set());
    expect(dropped).toEqual([{ snapId: 'snap-1', localId: 'a', candidateId: 'c-gone' }]);
  });

  it('forgives an explicitly dismissed row and a resolved provisional’s retired row', () => {
    const dropped = findDroppedSegments(
      [snap([{ localId: 'a', candidateId: 'c-skip' }])],
      [],
      [{ candidateId: 'c-skip', resolvedAt: '2026-10-26T00:00:00Z' } as never],
      new Set(['c-skip']),
    );
    expect(dropped).toEqual([]);
  });

  it('ignores unsent items: the receipt still shows them', () => {
    expect(findDroppedSegments([snap([{ localId: 'a' }])], [], [], new Set())).toEqual([]);
  });
});
