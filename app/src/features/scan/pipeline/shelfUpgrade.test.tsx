// The shelf upgrade (#809): after `identify` answers for a shelf snap, the server that hosts the library may read the same still and
// add what Apple's read missed. These tests drive a real SnapPipeline against the mock server (read-shelf.ts), with the phone's file
// pieces faked, and look at what left the phone and what the cards did.
import { waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { logger } from '@/lib/auth/log';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { shelfMock } from '@/mocks/read-shelf';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { patchCandidate } from '@/features/review/api';
import { reviewable } from '@/features/review/grouping';
import { skippedRows } from '@/features/review/skipped';
import { useCardsStore } from '../cards/cardsStore';
import { getDiscardOutbox } from '../session/discards';
import { routeSessionMessage, mergeSnapshotIntoCards } from '../session/sessionApi';
import { pictureLines } from './lines';
import { restPipelineApi } from './pipelineApi';
import { FIRST_OFFLINE_WAIT_MS, SHELF_MAX_TRIES, SHELF_TIMEOUT_MS } from './shelfUpgrade';
import { SnapPipeline } from './SnapPipeline';
import { FakeShelfFiles, makeKit, readItem, readingOf, resetKit } from './testKit';

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64' });
const zelda = readItem('Zelda: Ocarina of Time', { kind: 'game', platform: 'Nintendo 64' });

const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};

/** A kit whose pipeline can send shelf photos. */
function shelfKit(options: { enabled?: boolean; signals?: AbortSignal[] } = {}) {
  const kit = makeKit();
  const files = new FakeShelfFiles(kit.fs);
  files.enabled = options.enabled ?? true;
  kit.deps.shelf = files.shelf;
  let clock = 1_000_000;
  const { signals } = options;
  // Records the signal of every upload, so a test can see that the request itself was aborted.
  const api = signals
    ? { ...kit.deps.api, readShelf: async (...args: Parameters<typeof restPipelineApi.readShelf>) => (signals.push(args[3]!), restPipelineApi.readShelf(...args)) }
    : kit.deps.api;
  const pipeline = new SnapPipeline({ ...kit.deps, api, now: () => clock });
  return {
    ...kit,
    files,
    pipeline,
    advance: (ms: number) => {
      clock += ms;
    },
  };
}
type Kit = ReturnType<typeof shelfKit>;

const shelfShot = (kit: Kit) => kit.snapEvent('shutter', 'shelf_segment');
/** Has the pipeline nothing waiting in the shelf queue? */
const queueEmpty = (kit: Kit) => kit.store.getState().snaps.every((s) => !s.shelfUpload);
const settled = (kit: Kit) => waitFor(() => expect(queueEmpty(kit)).toBe(true));

/** A row the server's own read adds: unconfirmed, from `picture_server_model`. */
const serverRow = (title: string, seq: number): CandidateDto =>
  ({
    id: `5e5e5e5e-0000-4000-8000-${String(seq).padStart(12, '0')}`,
    seq,
    changeSeq: 100 + seq,
    source: 'picture_server_model',
    rawCode: null,
    symbology: null,
    visibleText: title,
    localId: null,
    box: null,
    boxSource: null,
    resolution: 'not_found',
    score: null,
    needsArt: false,
    needsCoverVerify: false,
    release: null,
    inferred: { title },
    alternatives: null,
    decision: 'undecided',
    copies: 1,
    alreadyOwnedCount: 0,
    ownedAt: [],
    createdItemId: null,
    userEdited: false,
    pendingReason: null,
    retryAfter: null,
    skipReason: null,
  }) as unknown as CandidateDto;

/** The server skips a phone row: in its own session too, with the next change number, as the real server does. */
const skippedByServer = (sessionId: string, row: CandidateDto): CandidateDto => {
  const session = scanMock.sessions.get(sessionId)!;
  const skipped = { ...row, decision: 'rejected', skipReason: 'server_did_not_see', changeSeq: ++session.seq } as CandidateDto;
  Object.assign(session.candidates.find((c) => c.id === row.id)!, skipped);
  return skipped;
};

const cards = () => Object.values(useCardsStore.getState().byId);
const skippedNow = () => Object.values(useCardsStore.getState().skipped);
/** The receipt lines of the kit's snaps, as the scanner builds them (cards plus skipped rows). */
const lineStatuses = (kit: Kit) =>
  pictureLines(kit.store.getState().snaps, [...cards(), ...skippedNow()], true).map((l) => l.status);
const cardFor = (title: string) => cards().find((c) => c.visibleText === title || c.release?.title === title);

beforeEach(() => {
  jest.spyOn(logger, 'warn').mockImplementation(() => undefined);
  scanMock.reset();
  identifyMock.reset();
  shelfMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
  jest.restoreAllMocks();
});

describe('what is sent', () => {
  it('sends a shelf snap to the server once, after identify has answered for it', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune, mario]));
    await kit.pipeline.onSnap(await shelfShot(kit));

    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    expect(identifyMock.requests).toHaveLength(1); // identify went first
    const [snap] = kit.store.getState().snaps;
    expect(shelfMock.requests[0]).toMatchObject({ snapId: snap!.snapId, sessionId: kit.tray.getState().sessionId, contentType: 'image/jpeg' });
    expect(shelfMock.requests[0]!.size).toBeGreaterThan(0);

    await settled(kit);
    await kit.pipeline.flush();
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(1); // once
  });

  it('sends only the picture: no part for the lines Vision read', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    expect(shelfMock.requests[0]!.parts).toEqual(['file']);
  });

  it('sends a photo of at most 2048 px on the long edge, re-encoded even when it is small already, and deletes the shrunk copy', async () => {
    const big = shelfKit();
    big.reader.script(readingOf([dune]));
    const event = await shelfShot(big);
    event.photo = { ...event.photo, width: 3000, height: 2250 };
    await big.pipeline.onSnap(event);
    await settled(big);
    expect(big.files.shrinks).toEqual([{ width: 2048, height: 1536 }]);
    expect(big.files.reads).toHaveLength(1);
    expect(big.files.reads[0]).not.toBe(big.snaps.list()[0]!.stillUri); // the shrunk copy went, not the stored still
    expect(big.fs.strays()).toEqual([]); // the shrunk copy is gone

    resetKit();
    const small = shelfKit();
    small.reader.script(readingOf([dune]));
    await small.pipeline.onSnap(await shelfShot(small)); // 2000 x 1500 already
    await settled(small);
    expect(small.files.shrinks).toHaveLength(1); // still passed through the re-encode
    expect(Math.max(...small.files.shrinks.flatMap((s) => [s.width, s.height]))).toBeLessThanOrEqual(2048);
    expect(small.fs.strays()).toEqual([]);
  });

  it.each(['single_cover', 'loose_item'] as const)('never sends a %s shot', async (shotKind) => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    await kit.pipeline.onSnap(await kit.snapEvent('shutter', shotKind));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(0);
    expect(kit.files.shrinks).toHaveLength(0);
  });

  it('never sends a cover snapped for one Review row', async () => {
    const kit = shelfKit({ enabled: false });
    kit.reader.script(readingOf([dune]), readingOf([dune]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(cards()).toHaveLength(1));
    const row = cards()[0]!;
    kit.files.enabled = true;
    await kit.pipeline.onSnap(await shelfShot(kit), { bound: { candidateId: row.id, onDone: () => undefined } });
    await waitFor(() => expect(identifyMock.requests).toHaveLength(2));
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(0);
  });

  it('sends nothing when the server says it has no shelf reader', async () => {
    const kit = shelfKit({ enabled: false });
    kit.reader.script(readingOf([dune]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(0);
    expect(kit.store.getState().snaps[0]!.shelfUpload).toBeUndefined();
  });

  it('sends nothing when the build has no shelf upload at all (the pipeline an older server gets)', async () => {
    const kit = makeKit();
    const pipeline = new SnapPipeline(kit.deps);
    kit.reader.script(readingOf([dune]));
    await pipeline.onSnap(await kit.snapEvent('shutter', 'shelf_segment'));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await pipeline.flush();
    expect(shelfMock.requests).toHaveLength(0);
  });
});

describe('what comes back', () => {
  const sendAndHold = async (kit: Kit) => {
    const answer = gate();
    shelfMock.hold = answer.hold;
    kit.reader.script(readingOf([dune, mario]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1)); // identify has answered; the server's read is out
    return answer;
  };

  it('adds the server’s rows to the cards without moving the rows already shown', async () => {
    const kit = shelfKit();
    const answer = await sendAndHold(kit);
    const before = cards().map((c) => c.id);
    const orderBefore = { ...useCardsStore.getState().order };
    const added = serverRow('Foundation', 7);
    shelfMock.rows = [added];
    answer.release();
    await settled(kit);

    await waitFor(() => expect(cardFor('Foundation')).toBeDefined());
    expect(cardFor('Foundation')).toMatchObject({ source: 'picture_server_model', decision: 'undecided' });
    const order = useCardsStore.getState().order;
    for (const id of before) expect(order[id]).toBe(orderBefore[id]); // nothing moved
    expect(order[added.id]).toBeGreaterThan(Math.max(...Object.values(orderBefore)));
    expect(cards().filter((c) => before.includes(c.id))).toHaveLength(before.length); // nothing disappeared
  });

  it('moves an untouched phone row to skipped, and Put back restores it', async () => {
    const kit = shelfKit();
    const answer = await sendAndHold(kit);
    const phoneRow = cardFor('Super Mario 64')!;
    const orderBefore = useCardsStore.getState().order[phoneRow.id];
    shelfMock.rows = [serverRow('Foundation', 7), skippedByServer(kit.tray.getState().sessionId!, phoneRow)];
    answer.release();
    await settled(kit);

    await waitFor(() => expect(useCardsStore.getState().skipped[phoneRow.id]?.decision).toBe('rejected'));
    expect(skippedRows(skippedNow()).map((r) => [r.candidate.id, r.reason])).toEqual([[phoneRow.id, 'server_did_not_see']]);
    expect(reviewable(cards()).map((c) => c.id)).not.toContain(phoneRow.id);
    expect(useCardsStore.getState().order[phoneRow.id]).toBe(orderBefore); // the list did not jump
    expect(lineStatuses(kit)).toEqual(['done', 'done']); // the receipt line stays settled

    await patchCandidate(kit.tray.getState().sessionId!, phoneRow.id, { decision: 'undecided' }); // Put back
    expect(reviewable(cards()).map((c) => c.id)).toContain(phoneRow.id);
    expect(skippedNow()).toEqual([]);
  });

  it('leaves a row the person has already acted on as it is', async () => {
    const kit = shelfKit();
    const answer = await sendAndHold(kit);
    const phoneRow = cardFor('Super Mario 64')!;
    useCardsStore.getState().setDecision(phoneRow.id, { ...phoneRow, decision: 'accepted' });
    shelfMock.rows = [skippedByServer(kit.tray.getState().sessionId!, phoneRow)];
    answer.release();
    await settled(kit);

    expect(useCardsStore.getState().byId[phoneRow.id]).toMatchObject({ decision: 'accepted' });
  });

  it('does not bring back a row the person discarded while the server was reading', async () => {
    const kit = shelfKit();
    const answer = await sendAndHold(kit);
    const added = serverRow('Foundation', 7);
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: kit.tray.getState().sessionId!, candidateId: added.id });
    shelfMock.rows = [added];
    answer.release();
    await settled(kit);
    expect(cardFor('Foundation')).toBeUndefined();
  });
});

describe('when the server cannot answer', () => {
  it('keeps Apple’s read, waits out Retry-After and asks again, at most 3 times in all', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune, mario]));
    shelfMock.refusals = [
      { status: 503, code: 'scan.reader_busy', retryAfter: 7 },
      { status: 503, code: 'scan.reader_unavailable', retryAfter: 7 },
      { status: 503, code: 'scan.reader_busy', retryAfter: 7 },
      { status: 503, code: 'scan.reader_busy', retryAfter: 7 },
    ];
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await waitFor(() => expect(kit.store.getState().snaps[0]!.shelfUpload).toBe(true)); // still waiting
    expect(cards()).toHaveLength(2); // Apple's read stands

    kit.advance(6_999);
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(1); // not before the server said

    kit.advance(1);
    await kit.pipeline.flush();
    await waitFor(() => expect(shelfMock.requests).toHaveLength(2));
    kit.advance(7_000);
    await kit.pipeline.flush();
    await waitFor(() => expect(shelfMock.requests).toHaveLength(SHELF_MAX_TRIES));
    await settled(kit); // given up

    kit.advance(60_000);
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(SHELF_MAX_TRIES);
    expect(cards()).toHaveLength(2);
    expect(kit.fs.strays()).toEqual([]);
  });

  it('stops for the session when the server says it has no reader, and fetches the config again', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]), readingOf([mario]));
    shelfMock.refusals = [{ status: 409, code: 'scan.reader_off' }];
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.refreshes).toBe(1));
    await settled(kit);

    await kit.pipeline.onSnap(await shelfShot(kit)); // a second shelf in the same scan
    await waitFor(() => expect(identifyMock.requests).toHaveLength(2));
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(1); // nothing more for this session
  });

  it.each([
    [404, undefined],
    [409, 'scan.not_a_shelf'],
    [409, 'scan.session_closed'],
    [400, undefined],
    [413, undefined],
    [415, undefined],
  ])('gives up quietly on a %s %s, keeping Apple’s read and sending nothing again', async (status, code) => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    shelfMock.refusals = [{ status, code }];
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await settled(kit);
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(1);
    expect(cards()).toHaveLength(1);
    expect(kit.tray.getState().problem).toBeNull(); // the person is told nothing
    expect(kit.fs.strays()).toEqual([]);
  });

  it('waits while offline and sends when the connection is back', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.reads).toHaveLength(1));
    await waitFor(() => expect(kit.fs.strays()).toEqual([])); // the failed send left no copy behind
    expect(kit.store.getState().snaps[0]!.shelfUpload).toBe(true); // waiting, not given up
    expect(shelfMock.requests).toHaveLength(0);

    kit.advance(FIRST_OFFLINE_WAIT_MS);
    await kit.pipeline.flush(); // the connection is back
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await settled(kit);
  });

  it('gives up after the client timeout, quietly', async () => {
    jest.useFakeTimers({ doNotFake: ['nextTick', 'setImmediate', 'queueMicrotask'] });
    try {
      const kit = shelfKit();
      kit.reader.script(readingOf([dune]));
      shelfMock.hold = new Promise(() => undefined); // the server never answers
      const sending = kit.pipeline.onSnap(await shelfShot(kit));
      await jest.advanceTimersByTimeAsync(100);
      await sending;
      await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
      await jest.advanceTimersByTimeAsync(SHELF_TIMEOUT_MS + 1);
      await waitFor(() => expect(queueEmpty(kit)).toBe(true));
      expect(cards()).toHaveLength(1);
      expect(kit.fs.strays()).toEqual([]);
    } finally {
      jest.useRealTimers();
    }
  });

  it('never puts a title or a file path in a warning', async () => {
    const warn = jest.spyOn(logger, 'warn').mockImplementation(() => undefined);
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    shelfMock.refusals = [{ status: 404 }];
    await kit.pipeline.onSnap(await shelfShot(kit));
    await settled(kit);
    expect(warn).toHaveBeenCalled();
    for (const [message] of warn.mock.calls) {
      expect(message).not.toMatch(/Dune|file:|\.jpg|\/cache|\/snaps/i);
    }
  });
});

describe('when the scan ends or the screen goes', () => {
  const inFlight = async (kit: Kit) => {
    const answer = gate();
    shelfMock.hold = answer.hold;
    kit.reader.script(readingOf([dune, mario]));
    kit.pipeline.start();
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    shelfMock.rows = [serverRow('Foundation', 7)];
    return answer;
  };

  it('keeps the still pinned while the server reads it', async () => {
    const kit = shelfKit();
    const answer = await inFlight(kit);
    const snapId = kit.store.getState().snaps[0]!.snapId;
    expect(kit.snaps.isPinned(snapId)).toBe(true);
    expect(kit.snaps.get(snapId)).toBeDefined();
    answer.release();
    await settled(kit);
    expect(kit.snaps.get(snapId)).toBeDefined(); // the receipt still has it until the scan ends
    kit.pipeline.stop();
  });

  it('stops a read in flight when the scanner is left, deleting the shrunk copy and drawing nothing', async () => {
    const kit = shelfKit();
    const answer = await inFlight(kit);
    kit.pipeline.stop();
    answer.release();
    await settled(kit);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(cardFor('Foundation')).toBeUndefined();
    expect(kit.fs.strays()).toEqual([]);
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(1); // and it does not start again
  });

  it('stops a read in flight when the scan is discarded', async () => {
    const kit = shelfKit();
    const answer = await inFlight(kit);
    kit.tray.getState().setSessionId(null); // Discard ends the session
    answer.release();
    await settled(kit);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(cardFor('Foundation')).toBeUndefined();
    expect(kit.fs.strays()).toEqual([]);
    kit.pipeline.stop();
  });

  it('stops a read in flight when the scan is added, and Add does not wait for it', async () => {
    const kit = shelfKit();
    await inFlight(kit);
    const started = Date.now();
    kit.tray.getState().closeSession(); // Add closes the session while the read is still out
    expect(Date.now() - started).toBeLessThan(1_000);
    await settled(kit); // nothing waits any more, and the read never came back
    expect(cardFor('Foundation')).toBeUndefined();
    kit.pipeline.stop();
  });

  it('stops when the library is switched (the scanner for the old library goes)', async () => {
    const kit = shelfKit();
    const answer = await inFlight(kit);
    kit.pipeline.stop(); // the same call a library switch makes
    answer.release();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(cardFor('Foundation')).toBeUndefined();
    expect(queueEmpty(kit)).toBe(true);
  });
});

describe('the request itself is aborted', () => {
  const held = async (kit: Kit) => {
    shelfMock.hold = gate().hold;
    kit.reader.script(readingOf([dune, mario]));
    kit.pipeline.start();
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
  };

  it.each<[string, (kit: Kit) => void]>([
    ['the scanner is left', (kit) => kit.pipeline.stop()],
    ['the scan is discarded', (kit) => kit.tray.getState().setSessionId(null)],
    ['the scan is added', (kit) => kit.tray.getState().closeSession()],
    ['the library is switched', (kit) => kit.pipeline.stop()],
  ])('aborts the upload in flight when %s', async (_name, trigger) => {
    const signals: AbortSignal[] = [];
    const kit = shelfKit({ signals });
    await held(kit);
    expect(signals).toHaveLength(1);
    expect(signals[0]!.aborted).toBe(false);
    trigger(kit);
    expect(signals[0]!.aborted).toBe(true);
    kit.pipeline.stop();
  });

  it('sends nothing at all for a snap whose scan is over before the send', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    await kit.pipeline.onSnap(await shelfShot(kit)); // not started: nobody watches the tray
    await waitFor(() => expect(kit.files.reads).toHaveLength(1));
    expect(kit.store.getState().snaps[0]!.shelfUpload).toBe(true);
    const shrinks = kit.files.shrinks.length;

    kit.tray.getState().setSessionId('c0000000-0000-4000-8000-0000000000c1'); // the scan ended meanwhile
    kit.advance(FIRST_OFFLINE_WAIT_MS);
    await kit.pipeline.flush();
    await waitFor(() => expect(queueEmpty(kit)).toBe(true));
    expect(shelfMock.requests).toHaveLength(0);
    expect(kit.files.shrinks).toHaveLength(shrinks); // not even prepared
  });
});

describe('one rule for a row the server skipped, by any path and in any order', () => {
  const startHeld = async (kit: Kit) => {
    const answer = gate();
    shelfMock.hold = answer.hold;
    kit.reader.script(readingOf([dune, mario]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    const phoneRow = cardFor('Super Mario 64')!;
    const other = cardFor('Dune')!;
    const skipped = skippedByServer(kit.tray.getState().sessionId!, phoneRow);
    shelfMock.rows = [skipped];
    return { answer, phoneRow, other, skipped, sessionId: kit.tray.getState().sessionId! };
  };
  const event = (sessionId: string, row: CandidateDto) =>
    routeSessionMessage(sessionId, { event: 'candidate.updated', id: String(row.changeSeq), data: JSON.stringify(row) });
  const summary = (kit: Kit) => ({
    cards: cards().map((c) => c.id).sort(),
    skipped: skippedNow().map((c) => [c.id, c.skipReason]),
    lines: lineStatuses(kit),
  });
  const expected = (s: Awaited<ReturnType<typeof startHeld>>) => ({
    cards: [s.other.id],
    skipped: [[s.phoneRow.id, 'server_did_not_see']],
    lines: ['done', 'done'],
  });

  it('answer, then the event', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    s.answer.release();
    await settled(kit);
    event(s.sessionId, s.skipped);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('the event, then the answer', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    event(s.sessionId, s.skipped);
    s.answer.release();
    await settled(kit);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('the event twice', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    event(s.sessionId, s.skipped);
    event(s.sessionId, s.skipped);
    s.answer.release();
    await settled(kit);
    event(s.sessionId, s.skipped);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('the snapshot after the answer', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    s.answer.release();
    await settled(kit);
    mergeSnapshotIntoCards(scanMock.sessions.get(s.sessionId)!.candidates);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('the snapshot after the event', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    event(s.sessionId, s.skipped);
    mergeSnapshotIntoCards(scanMock.sessions.get(s.sessionId)!.candidates);
    expect(summary(kit)).toEqual(expected(s));
    s.answer.release();
    await settled(kit);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('a replayed older copy of the row does not bring it back', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    s.answer.release();
    await settled(kit);
    event(s.sessionId, { ...s.phoneRow, changeSeq: s.phoneRow.changeSeq } as CandidateDto);
    expect(summary(kit)).toEqual(expected(s));
  });

  it('a row the person removed while the server was reading does not come back, and is not in Skipped', async () => {
    const kit = shelfKit();
    const s = await startHeld(kit);
    kit.pipeline.removeItem(kit.store.getState().snaps[0]!.snapId, s.phoneRow.localId!);
    expect(cardFor('Super Mario 64')).toBeUndefined();
    s.answer.release();
    await settled(kit);
    expect(cardFor('Super Mario 64')).toBeUndefined();
    expect(skippedNow()).toEqual([]);
  });
});

describe('retries, the connection and the photo', () => {
  it.each([
    [0, 1_000],
    [7, 7_000],
    [600, 60_000],
  ])('after a 503 with Retry-After %s, sends nothing before %s ms and one request after (clamped to 1 to 60 s)', async (retryAfter, waitMs) => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    shelfMock.refusals = [{ status: 503, code: 'scan.reader_busy', retryAfter }];
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await waitFor(() => expect(kit.fs.strays()).toEqual([]));
    kit.advance(waitMs - 1);
    await kit.pipeline.flush();
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(shelfMock.requests).toHaveLength(1);
    kit.advance(1);
    await kit.pipeline.flush();
    await waitFor(() => expect(shelfMock.requests).toHaveLength(2));
    await settled(kit);
  });

  it('does not count a failure to connect as a try: all 3 tries are still there afterwards', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    shelfMock.refusals = [1, 2, 3, 4].map(() => ({ status: 503, retryAfter: 1 }));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.reads).toHaveLength(1));
    await waitFor(() => expect(kit.fs.strays()).toEqual([]));
    kit.advance(FIRST_OFFLINE_WAIT_MS);
    await kit.pipeline.flush(); // offline again
    await waitFor(() => expect(kit.files.reads).toHaveLength(2));
    await waitFor(() => expect(kit.fs.strays()).toEqual([]));
    expect(shelfMock.requests).toHaveLength(0);

    for (let i = 1; i <= 3; i += 1) {
      kit.advance(120_000);
      await kit.pipeline.flush();
      await waitFor(() => expect(shelfMock.requests).toHaveLength(i));
      await waitFor(() => expect(kit.fs.strays()).toEqual([]));
    }
    await settled(kit); // given up after the third try
    kit.advance(120_000);
    await kit.pipeline.flush();
    expect(shelfMock.requests).toHaveLength(SHELF_MAX_TRIES);
  });

  it('does not send a shrunk photo over 4 MB, and gives that snap up quietly', async () => {
    const kit = shelfKit();
    kit.files.bytes = 4 * 1024 * 1024 + 1;
    kit.reader.script(readingOf([dune]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await settled(kit);
    expect(shelfMock.requests).toHaveLength(0);
    expect(kit.files.discarded).toHaveLength(1); // the shrunk copy went
    expect(kit.fs.strays()).toEqual([]);
    expect(cards()).toHaveLength(1);
  });

  it('does not shrink and re-encode the photo again with every flush while there is no connection', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error()));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.shrinks).toHaveLength(1));
    for (let tick = 0; tick < 5; tick += 1) {
      kit.advance(5_000);
      await kit.pipeline.flush();
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
    expect(kit.files.shrinks).toHaveLength(1);
    expect(kit.store.getState().snaps[0]!.shelfUpload).toBe(true); // still waiting
  });

  it('never posts or deletes the stored still when the shrink hands it back unchanged', async () => {
    const kit = shelfKit();
    kit.files.shelf.shrink = async (photo) => ({ uri: photo.uri, width: photo.width, height: photo.height });
    kit.reader.script(readingOf([dune]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await settled(kit);
    expect(shelfMock.requests).toHaveLength(0);
    expect(kit.files.discarded).toEqual([]);
    expect(kit.fs.live()).toContain(kit.snaps.list()[0]!.stillUri);
  });
});

describe('the queue', () => {
  it('sends one at a time, oldest first', async () => {
    const kit = shelfKit();
    const answer = gate();
    shelfMock.hold = answer.hold;
    kit.reader.script(readingOf([dune]), readingOf([mario]), readingOf([zelda]));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(3));
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(shelfMock.requests).toHaveLength(1); // the others wait for the first

    answer.release();
    await waitFor(() => expect(shelfMock.requests).toHaveLength(3));
    await settled(kit);
    const sentOrder = identifyMock.requests.map((r) => r.body.snapId);
    expect(shelfMock.requests.map((r) => r.snapId)).toEqual(sentOrder);
  });

  it('sends what was waiting when the app is killed and comes back', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.reads).toHaveLength(1));
    expect(kit.store.getState().snaps[0]!.shelfUpload).toBe(true);
    expect(shelfMock.requests).toHaveLength(0);

    // The app is killed: no stop(), no cleanup. A new pipeline opens on what was saved.
    const returned = new SnapPipeline(kit.deps);
    returned.start();
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    await settled(kit);
    returned.stop();
  });

  it('does not send a waiting upload after a kill when the server has since lost its reader', async () => {
    const kit = shelfKit();
    kit.reader.script(readingOf([dune]));
    server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
    await kit.pipeline.onSnap(await shelfShot(kit));
    await waitFor(() => expect(kit.files.reads).toHaveLength(1));
    kit.files.enabled = false;
    const returned = new SnapPipeline(kit.deps);
    returned.start();
    await settled(kit);
    expect(shelfMock.requests).toHaveLength(0);
    returned.stop();
  });
});
