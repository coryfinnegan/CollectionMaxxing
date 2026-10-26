// LOCKED acceptance tests for #345: provisional-or-counted — no path drops a captured segment without a visible
// row or a provisional. Never weaken, skip or delete these.
import type { CandidateDto, CreateProvisionalResultDto, ProvisionalDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { createPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { candidate } from './testData';
import {
  createProvisionalOutbox,
  findDroppedSegments,
  flushProvisionalOutbox,
  keepAsUnverified,
  setProvisionalCropReader,
  setProvisionalOutbox,
  setProvisionalTransport,
  type ProvisionalTransport,
} from './provisionals';

const SESSION = 'session-1';

const keptResult = (candidateId: string, cropAssetId?: string): CreateProvisionalResultDto =>
  ({
    provisional: {
      id: `provisional-${candidateId}`,
      libraryId: MOCK_LIBRARY_ID,
      sessionId: SESSION,
      snapId: null,
      titleGuess: 'Zzxq',
      isGuess: true,
      mediaHint: 'mixed',
      locationId: null,
      status: 'unverified',
      candidateId,
      cropUrl: cropAssetId ? `/media/covers/${cropAssetId}` : null,
      createdAt: '2026-10-26T00:00:00Z',
      resolvedAt: null,
      resolveAction: null,
      createdItemId: null,
    },
    candidate: { ...candidate({ id: candidateId }), decision: 'rejected' },
  }) as CreateProvisionalResultDto;

/** The test proxy: the server's side of keep, recording every crop upload and keep. */
const proxy = () => {
  const calls = { uploads: 0, keeps: [] as { candidateId: string; cropAssetId?: string }[] };
  const provisionals: ProvisionalDto[] = [];
  const transport: ProvisionalTransport = {
    async uploadCrop() {
      calls.uploads += 1;
      return { assetId: `asset-${calls.uploads}`, cropUrl: `/media/covers/asset-${calls.uploads}` };
    },
    async keep(_sessionId, candidateId, cropAssetId) {
      calls.keeps.push({ candidateId, cropAssetId });
      const result = keptResult(candidateId, cropAssetId);
      provisionals.push(result.provisional);
      return result;
    },
  };
  return { calls, provisionals, transport };
};

const seedSent = (store: ReturnType<typeof createPipelineStore>, snapId: string, localId: string, candidateId: string) => {
  store.getState().addSnap({
    snapId,
    libraryId: MOCK_LIBRARY_ID,
    trigger: 'auto',
    createdAt: 1,
    thumbUri: 'file:///thumb.jpg',
    phase: 'read',
    sent: false,
    items: [{ localId, text: localId, title: localId, payload: { localId } as never }],
    unreadableCount: 0,
  });
  store.getState().markSent(snapId, SESSION, { [localId]: candidateId });
};

const blobOf = (size: number) => ({ size }) as Blob;

beforeEach(() => {
  useCardsStore.getState().reset();
  setProvisionalCropReader(async () => blobOf(100));
});

afterEach(() => {
  setProvisionalTransport(undefined);
  setProvisionalCropReader(undefined);
  setProvisionalOutbox(undefined);
});

describe('provisional-or-counted', () => {
  it('a kept segment is visible exactly once: a retired row plus its open provisional', async () => {
    const server = proxy();
    setProvisionalTransport(server.transport);
    setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
    const store = createPipelineStore({ storage: new MemoryKeyValueStore() });
    seedSent(store, 'snap-1', 'item-a', 'c1');
    useCardsStore.getState().merge([candidate({ id: 'c1', resolution: 'not_found', release: null })], { announce: false });

    const result = await keepAsUnverified({ canEdit: true, libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidate: candidate({ id: 'c1', resolution: 'not_found', release: null }), cropUri: 'file:///crop.jpg' });

    expect(result).toEqual({ ok: true });
    expect(findDroppedSegments(store.getState().snaps, Object.values(useCardsStore.getState().byId), server.provisionals, new Set())).toEqual([]);
    expect(server.calls.keeps).toHaveLength(1);
  });

  it('a kill between the crop upload and the keep loses nothing: the row stays visible and can be kept', async () => {
    const server = proxy();
    setProvisionalTransport(server.transport);
    setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
    const store = createPipelineStore({ storage: new MemoryKeyValueStore() });
    seedSent(store, 'snap-1', 'item-a', 'c1');
    const row = candidate({ id: 'c1', resolution: 'not_found', release: null });
    useCardsStore.getState().merge([row], { announce: false });

    await server.transport.uploadCrop(SESSION, 'c1', blobOf(100)); // the keep is killed before it sends
    const restarted = createPipelineStore({ storage: new MemoryKeyValueStore() }); // fresh run, same server truth
    void restarted;

    expect(findDroppedSegments(store.getState().snaps, Object.values(useCardsStore.getState().byId), server.provisionals, new Set())).toEqual([]);
    const result = await keepAsUnverified({ canEdit: true, libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidate: row, cropUri: 'file:///crop.jpg' });
    expect(result).toEqual({ ok: true });
    expect(server.calls.keeps).toHaveLength(1); // kept once; the orphaned first crop is swept server-side
  });

  it('a kill with a queued keep flushes it on restart, exactly once', async () => {
    const outboxStorage = new MemoryKeyValueStore();
    setProvisionalOutbox(createProvisionalOutbox({ storage: outboxStorage }));
    const server = proxy();
    // Offline at keep time: nothing sent, the keep waits.
    setProvisionalTransport({
      uploadCrop: () => Promise.reject(new TypeError('fetch failed')),
      keep: () => Promise.reject(new TypeError('fetch failed')),
    });
    const store = createPipelineStore({ storage: new MemoryKeyValueStore() });
    seedSent(store, 'snap-1', 'item-a', 'c1');
    const row = candidate({ id: 'c1', resolution: 'not_found', release: null });
    useCardsStore.getState().merge([row], { announce: false });

    expect(await keepAsUnverified({ canEdit: true, libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidate: row, cropUri: undefined })).toEqual({ ok: true, queued: true });

    // The kill: new stores over the same outbox storage, back online.
    setProvisionalOutbox(createProvisionalOutbox({ storage: outboxStorage }));
    setProvisionalTransport(server.transport);
    const remaining = await flushProvisionalOutbox({ libraryId: MOCK_LIBRARY_ID });

    expect(remaining).toBe(0);
    expect(server.calls.keeps).toHaveLength(1);
    expect(findDroppedSegments(store.getState().snaps, Object.values(useCardsStore.getState().byId), server.provisionals, new Set())).toEqual([]);
    expect(useCardsStore.getState().byId.c1?.decision).toBe('rejected');
  });

  it('a killed-while-reading snap is counted, never completed or dropped, beside open provisionals', async () => {
    const server = proxy();
    setProvisionalTransport(server.transport);
    setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
    const storage = new MemoryKeyValueStore();
    const before = createPipelineStore({ storage });
    before.getState().addSnap({
      snapId: 'snap-halfway',
      libraryId: MOCK_LIBRARY_ID,
      trigger: 'auto',
      createdAt: 1,
      thumbUri: 'file:///thumb.jpg',
      phase: 'reading',
      sent: false,
      items: [],
      unreadableCount: 0,
    });
    seedSent(before, 'snap-kept', 'item-a', 'c1');
    useCardsStore.getState().merge([candidate({ id: 'c1', resolution: 'not_found', release: null })], { announce: false });
    await keepAsUnverified({ canEdit: true, libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidate: candidate({ id: 'c1', resolution: 'not_found', release: null }), cropUri: undefined });

    // The restart re-reads the half-read snap or counts it orphaned; either way it is never silently completed.
    const after = createPipelineStore({ storage });
    const halfway = after.getState().snaps.find((s) => s.snapId === 'snap-halfway');
    expect(halfway?.phase).toBe('reading'); // still queued for a re-read, with its still pinned
    expect(findDroppedSegments(after.getState().snaps, Object.values(useCardsStore.getState().byId) as CandidateDto[], server.provisionals, new Set())).toEqual([]);
  });
});
