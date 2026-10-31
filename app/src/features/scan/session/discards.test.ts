// A discarded scan stays discarded: the reject the phone owes the server waits in an outbox (MMKV) until it
// lands, and discarded candidates never render again — not from a snapshot, not from the event stream.
import { http, HttpResponse } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { createPipelineStore, getPipelineStore, setPipelineStore, type PicSnap } from '@/features/scan/pipeline/pipelineStore';
import { createTrayStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { ApiError } from '@/lib/http';
import {
  createDiscardOutbox,
  dropCandidateLocally,
  dropPictureItemLocally,
  flushDiscardOutbox,
  getDiscardOutbox,
  isDiscarded,
  pruneDiscarded,
  setDiscardOutbox,
  withoutDiscarded,
} from './discards';

const SESSION = 'a0000000-0000-4000-8000-000000000001';

const candidate = (id: string, extra: Partial<CandidateDto> = {}): CandidateDto =>
  ({
    id,
    seq: 1,
    source: 'barcode',
    rawCode: `code-${id}`,
    symbology: 'ean-13',
    visibleText: null,
    localId: null,
    box: null,
    boxSource: null,
    resolution: 'not_found',
    tier: null,
    score: 0,
    artDistance: null,
    needsArt: false,
    needsCoverVerify: false,
    release: null,
    inferred: null,
    alternatives: null,
    decision: 'undecided',
    copies: 1,
    alreadyOwnedCount: 0,
    ownedAt: [],
    createdItemId: null,
    changeSeq: 1,
    edits: null,
    userEdited: false,
    pendingReason: null,
    retryAfter: null,
    shotKind: 'barcode',
    segmentId: null,
    guidanceCode: 'cover',
    cropUrl: null,
    ...extra,
  }) as CandidateDto;

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

const snap = (overrides: Partial<PicSnap> = {}): PicSnap => ({
  snapId: 'snap-1',
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter',
  createdAt: 1,
  thumbUri: 'file:///tmp/snap.jpg',
  phase: 'read',
  sent: true,
  sessionId: SESSION,
  items: [{ localId: 'item-1', text: 'Dune', title: 'Dune', payload: { localId: 'item-1', text: 'Dune' }, candidateId: 'cand-1' }],
  unreadableCount: 0,
  ...overrides,
});

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  setDiscardOutbox(undefined);
  setPipelineStore(undefined);
});

describe('discard outbox', () => {
  it('keeps a queued discard across a restart (a new outbox on the same storage)', () => {
    const storage = new MemoryKeyValueStore();
    createDiscardOutbox({ storage }).enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });

    const reopened = createDiscardOutbox({ storage });
    expect(reopened.list(MOCK_LIBRARY_ID).map((e) => e.candidateId)).toEqual(['cand-1']);
  });

  it('queues each candidate once and keeps libraries apart', () => {
    const queue = getDiscardOutbox();
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    queue.enqueue({ libraryId: 'other-library', sessionId: SESSION, candidateId: 'cand-2' });

    expect(queue.list(MOCK_LIBRARY_ID).map((e) => e.candidateId)).toEqual(['cand-1']);
  });

  it('hides discarded candidates from snapshots and the stream, until the server confirms the reject', () => {
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    const rows = [candidate('cand-1'), candidate('cand-2')];

    expect(isDiscarded('cand-1')).toBe(true);
    expect(isDiscarded('cand-2')).toBe(false);
    expect(withoutDiscarded(rows).map((c) => c.id)).toEqual(['cand-2']);

    pruneDiscarded([candidate('cand-1', { decision: 'rejected' })]);
    expect(isDiscarded('cand-1')).toBe(false);
    expect(withoutDiscarded(rows).map((c) => c.id)).toEqual(['cand-1', 'cand-2']);
  });

  it('rejects queued discards oldest first and drops them once they land', async () => {
    const first = candidate('cand-1');
    const second = candidate('cand-2');
    seed([first, second]);
    const queue = getDiscardOutbox();
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-2' });

    await expect(flushDiscardOutbox({ libraryId: MOCK_LIBRARY_ID })).resolves.toBe(0);
    expect(scanMock.sessions.get(SESSION)!.candidates.map((c) => c.decision)).toEqual(['rejected', 'rejected']);
    expect(queue.list(MOCK_LIBRARY_ID)).toHaveLength(0);
  });

  it('drops a discard the server refuses and still sends the rest', async () => {
    seed([candidate('cand-2')]);
    const queue = getDiscardOutbox();
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'gone' });
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-2' });

    await expect(flushDiscardOutbox({ libraryId: MOCK_LIBRARY_ID })).resolves.toBe(0);
    expect(scanMock.sessions.get(SESSION)!.candidates[0]!.decision).toBe('rejected');
    expect(queue.list(MOCK_LIBRARY_ID)).toHaveLength(0);
  });

  it.each([401, 408, 429])('keeps a discard the server answers with %i (try again later) and stops, like a lost connection', async (status) => {
    const queue = getDiscardOutbox();
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-2' });
    const reject = jest.fn().mockRejectedValue(new ApiError(status, {}));

    await expect(flushDiscardOutbox({ libraryId: MOCK_LIBRARY_ID, reject })).resolves.toBe(2);

    expect(reject).toHaveBeenCalledTimes(1); // it stopped at the first
    expect(queue.list(MOCK_LIBRARY_ID).map((e) => e.candidateId)).toEqual(['cand-1', 'cand-2']);
  });

  it.each([403, 404, 409])('drops a discard the server refuses for good (%i) and still sends the rest', async (status) => {
    const queue = getDiscardOutbox();
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    queue.enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-2' });
    const reject = jest.fn().mockRejectedValueOnce(new ApiError(status, {})).mockResolvedValueOnce(undefined);

    await expect(flushDiscardOutbox({ libraryId: MOCK_LIBRARY_ID, reject })).resolves.toBe(0);

    expect(reject).toHaveBeenCalledTimes(2);
    expect(queue.list(MOCK_LIBRARY_ID)).toHaveLength(0);
  });

  it('stops on a lost connection and keeps the rest queued', async () => {
    seed([candidate('cand-1')]);
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: 'cand-1' });
    server.use(http.patch('*/api/v1/scan-sessions/:id/candidates/:candidateId', () => HttpResponse.error()));

    await expect(flushDiscardOutbox({ libraryId: MOCK_LIBRARY_ID })).resolves.toBe(1);
    expect(scanMock.sessions.get(SESSION)!.candidates[0]!.decision).toBe('undecided');
    expect(isDiscarded('cand-1')).toBe(true);
  });
});

describe('dropPictureItemLocally', () => {
  it('takes the picture item of one candidate out of the snaps of its session, and nothing else', () => {
    setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
    getPipelineStore().getState().addSnap(snap());
    getPipelineStore().getState().addSnap(snap({ snapId: 'snap-2', sessionId: 'another-session' }));
    useCardsStore.getState().showLocal(candidate('local:snap-1:item-1', { localId: 'item-1' }));

    dropPictureItemLocally('a0000000-0000-4000-8000-000000000001', candidate('cand-1', { localId: 'item-1' }));

    expect(getPipelineStore().getState().snaps.map((s) => s.snapId)).toEqual(['snap-2']);
    expect(useCardsStore.getState().local['local:snap-1:item-1']).toBeUndefined();
  });
});

describe('dropCandidateLocally', () => {
  it('removes the card, the tray line and the snap item, so a restart finds nothing to resend', () => {
    const tray = createTrayStore({ storage: new MemoryKeyValueStore() });
    tray.getState().scan({ value: 'code-cand-1', symbology: 'ean-13' });
    setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
    getPipelineStore().getState().addSnap(snap());
    useCardsStore.getState().merge([candidate('cand-1', { rawCode: 'code-cand-1', localId: 'item-1' })], { announce: false });

    dropCandidateLocally({ tray, libraryId: MOCK_LIBRARY_ID, candidate: candidate('cand-1', { rawCode: 'code-cand-1', localId: 'item-1' }) });

    expect(useCardsStore.getState().byId['cand-1']).toBeUndefined();
    expect(tray.getState().lines).toHaveLength(0);
    expect(getPipelineStore().getState().snaps).toHaveLength(0);
  });

  it('leaves other libraries and other rows alone', () => {
    const tray = createTrayStore({ storage: new MemoryKeyValueStore() });
    tray.getState().scan({ value: 'code-cand-1', symbology: 'ean-13' });
    tray.getState().scan({ value: 'code-cand-9', symbology: 'ean-13' });
    setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
    getPipelineStore().getState().addSnap(snap());
    getPipelineStore().getState().addSnap(snap({ snapId: 'snap-2', libraryId: 'other-library' }));
    useCardsStore.getState().merge([candidate('cand-1'), candidate('cand-9')], { announce: false });

    dropCandidateLocally({ tray, libraryId: MOCK_LIBRARY_ID, candidate: candidate('cand-1', { rawCode: 'code-cand-1', localId: 'item-1' }) });

    expect(tray.getState().lines.map((l) => l.code)).toEqual(['code-cand-9']);
    expect(getPipelineStore().getState().snaps.map((s) => s.snapId)).toEqual(['snap-2']);
    expect(useCardsStore.getState().byId['cand-9']).toBeDefined();
  });
});
