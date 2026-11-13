// The shelf upgrade (#809, ADR-033): after `identify` has answered for a shelf snap, and while the server that hosts the library
// says it can read shelves, the snap's still goes to the server's own reader, one at a time, oldest first. Apple's read has been
// on the screen all along; what comes back only adds rows the phone missed or moves untouched phone rows to "skipped". There is no
// mode, no toggle and no spinner, and every failure is silent: Apple's read stands.
//
// What leaves the phone is the still, shrunk to 2048 px and re-encoded as a JPEG (the photo's hidden details do not survive that),
// and the shrunk copy is deleted afterwards. Nothing in a warning or a log carries a title or a file path.
import type { CandidateDto } from '@/api/generated/models';
import { logger } from '@/lib/auth/log';
import { ApiError } from '@/lib/http';
import { fitLongEdge } from '../autosnap/snapPhoto';
import { useCardsStore } from '../cards/cardsStore';
import { withoutDiscarded } from '../session/discards';
import { announce } from '../session/sessionApi';
import { isSingleItemShot } from '../shotkind/shotKind';
import { MAX_VERIFY_BYTES, type CoverVerifyDeps } from './coverVerify';
import type { PicSnap } from './pipelineStore';
import type { PipelineDeps } from './SnapPipeline';

/** The long edge the still is shrunk to before it leaves the phone (the server takes up to 4096 px). */
export const SHELF_LONG_EDGE = 2048;
/** How long one upload may take. A read takes 12 to 30 seconds, sometimes longer after the server has been idle. */
export const SHELF_TIMEOUT_MS = 150_000;
/** A busy or unavailable server is asked this many times in all, with the wait it names between them. */
export const SHELF_MAX_TRIES = 3;
const DEFAULT_WAIT_MS = 5_000;
const MIN_WAIT_MS = 1_000;
const MAX_WAIT_MS = 60_000;
/** With no connection the next try waits this long, doubling to the cap. */
export const FIRST_OFFLINE_WAIT_MS = 30_000;
const MAX_OFFLINE_WAIT_MS = 5 * 60_000;

/** What the pipeline needs to send a shelf snap: the same file pieces :verify-cover uses, and what the server says it can do. */
export interface ShelfUploadDeps extends CoverVerifyDeps {
  /** Whether the server that hosts the library can read shelves (its recognition config's `shelfReader`). */
  enabled(): boolean;
  /** Asks the server for its config again; called when it answers that it has no reader after all. */
  refresh(): Promise<unknown>;
}

type Outcome = 'done' | 'gave_up' | 'retry_later' | 'wait' | 'reader_off' | 'aborted';

const codeIs = (error: ApiError, code: string) => error.problem.code === code || (error.problem.type?.endsWith(code) ?? false);

/** A shelf snap: not a single cover, a loose item or a barcode, and not a cover shot for one Review row. */
export const isShelfSnap = (snap: Pick<PicSnap, 'shotKind' | 'boundCandidateId'>) =>
  !isSingleItemShot(snap.shotKind) && snap.shotKind !== 'barcode' && !snap.boundCandidateId;

export class ShelfUpgrades {
  private running: Promise<void> | undefined;
  private stopped = false;
  private inFlight: { snapId: string; sessionId: string; controller: AbortController } | undefined;
  /** Sends made so far per snap this run, and when a snap the server was too busy for may be tried again. */
  private readonly tries = new Map<string, number>();
  private readonly retryAt = new Map<string, number>();
  /** How many times in a row a send found no connection, per snap. */
  private readonly waits = new Map<string, number>();
  /** The sessions whose server answered "no reader": nothing more goes for them. */
  private readonly readerOff = new Set<string>();

  constructor(
    private readonly deps: PipelineDeps,
    private readonly now: () => number,
    private readonly isCurrent: (sessionId: string) => boolean,
  ) {}

  private get shelf() {
    return this.deps.shelf;
  }

  /** The scanner opened (or reopened): a snap a killed app left waiting is sent now. */
  begin() {
    this.stopped = false;
    this.run();
  }

  /** Leaving the scanner or a library switch: what is in flight is aborted and nothing waits any more. */
  stop() {
    this.stopped = true;
    this.inFlight?.controller.abort();
    this.clearAll();
  }

  /** `identify` answered for this snap: it joins the queue when it is a shelf and the server can read shelves. */
  consider(snapId: string) {
    const snap = this.deps.store.getState().snaps.find((s) => s.snapId === snapId);
    if (!snap || !snap.sessionId || !this.shelf?.enabled() || this.readerOff.has(snap.sessionId) || !isShelfSnap(snap)) return;
    this.deps.store.getState().setShelfUpload(snapId, true);
    this.deps.snaps.pin(snapId); // the still stays until the upgrade is done or given up
    this.run();
  }

  /** Sends what is due. Safe to call at any time; one upload runs at a time. */
  run() {
    if (this.stopped || this.running) return;
    this.running = this.loop().finally(() => {
      this.running = undefined;
    });
  }

  /** The tray's session changed (Discard, Add) or a snap went: an upload for a scan that is over is aborted, the queue for it cleared. */
  recheck() {
    const flight = this.inFlight;
    const snaps = this.deps.store.getState().snaps;
    if (flight && (!this.isCurrent(flight.sessionId) || !snaps.some((s) => s.snapId === flight.snapId))) flight.controller.abort();
    for (const snap of snaps) {
      if (snap.shelfUpload && (!snap.sessionId || !this.isCurrent(snap.sessionId))) this.deps.store.getState().setShelfUpload(snap.snapId, false);
    }
  }

  private waiting(): PicSnap[] {
    return this.deps.store
      .getState()
      .snaps.filter((s) => s.libraryId === this.deps.libraryId && s.shelfUpload && s.sent)
      .sort((a, b) => a.createdAt - b.createdAt);
  }

  private clearAll() {
    for (const snap of this.deps.store.getState().snaps) {
      if (snap.libraryId === this.deps.libraryId && snap.shelfUpload) this.deps.store.getState().setShelfUpload(snap.snapId, false);
    }
  }

  private async loop() {
    for (;;) {
      if (this.stopped) return;
      const next = this.waiting()[0];
      if (!next) return;
      if (!this.shelf?.enabled()) {
        this.clearAll(); // the server cannot read shelves (any more): Apple's read stands
        return;
      }
      if ((this.retryAt.get(next.snapId) ?? 0) > this.now()) return; // the oldest waits out its Retry-After; nothing jumps the queue
      const outcome = await this.upload(next);
      if (outcome === 'wait' || outcome === 'retry_later') return; // the next flush tries again
      if (outcome === 'reader_off') {
        const sessionId = next.sessionId;
        if (sessionId) this.readerOff.add(sessionId);
        this.clearAll();
        void this.shelf?.refresh().catch(() => undefined); // the server's config says what it can do now
        return;
      }
    }
  }

  private async upload(snap: PicSnap): Promise<Outcome> {
    const { store, snaps, api } = this.deps;
    const shelf = this.shelf;
    const sessionId = snap.sessionId;
    const stored = snaps.get(snap.snapId);
    if (!shelf || !sessionId || !this.isCurrent(sessionId) || !stored) return this.finish(snap.snapId, 'gave_up');
    const controller = new AbortController();
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, SHELF_TIMEOUT_MS);
    this.inFlight = { snapId: snap.snapId, sessionId, controller };
    let sent: string | undefined;
    let posting = false;
    try {
      const target = fitLongEdge(stored.width, stored.height, SHELF_LONG_EDGE);
      // Always re-encoded, even when it is small enough: that is what leaves the photo's hidden details behind.
      const shrunk = await shelf.shrink({ uri: stored.stillUri, width: stored.width, height: stored.height, orientation: 1 }, target);
      // The stored still is never posted and never deleted as if it were the shrunk copy.
      if (shrunk.uri === stored.stillUri) return this.finish(snap.snapId, 'gave_up', 'the photo was not re-encoded');
      sent = shrunk.uri;
      if (controller.signal.aborted) return this.finish(snap.snapId, 'aborted');
      const file = await shelf.readFile(shrunk.uri);
      if (file.size > MAX_VERIFY_BYTES) return this.finish(snap.snapId, 'gave_up'); // the server takes up to 4 MB
      posting = true;
      this.tries.set(snap.snapId, (this.tries.get(snap.snapId) ?? 0) + 1);
      const rows = await api.readShelf(sessionId, snap.sendId ?? snap.snapId, file, controller.signal);
      if (controller.signal.aborted || !this.isCurrent(sessionId) || !store.getState().snaps.some((s) => s.snapId === snap.snapId))
        return this.finish(snap.snapId, 'aborted');
      this.merge(rows);
      return this.finish(snap.snapId, 'done');
    } catch (error) {
      if (timedOut) return this.finish(snap.snapId, 'gave_up', 'timed out');
      if (controller.signal.aborted) return this.finish(snap.snapId, 'aborted');
      if (!posting) return this.finish(snap.snapId, 'gave_up', 'the photo could not be prepared'); // a file problem is not the connection's
      return this.failed(snap.snapId, error);
    } finally {
      clearTimeout(timer);
      this.inFlight = undefined;
      if (sent) await shelf.discard(sent).catch(() => undefined); // the shrunk copy is always deleted
    }
  }

  private failed(snapId: string, error: unknown): Outcome {
    if (!(error instanceof ApiError)) return this.waitForConnection(snapId); // fetch itself failed: no connection; the upload goes when it is back
    const status = error.status;
    if (status === 409 && codeIs(error, 'scan.reader_off')) return 'reader_off';
    if (status === 401) return this.waitForConnection(snapId); // signed out: the sign-in sends it
    if (status === 429 || status >= 500) {
      // Busy, unavailable or away (Retry-After): Apple's read stands and the same picture is asked again, a few times.
      if ((this.tries.get(snapId) ?? 0) >= SHELF_MAX_TRIES) return this.finish(snapId, 'gave_up', `status ${status} after ${SHELF_MAX_TRIES} tries`);
      this.retryAt.set(snapId, this.now() + Math.min(MAX_WAIT_MS, Math.max(MIN_WAIT_MS, error.retryAfterMs ?? DEFAULT_WAIT_MS)));
      return 'retry_later';
    }
    // 404 (the server does not know the snap), 409 (not a shelf, the session is closed), 400, 413, 415 and the like: asking
    // again would only be refused again.
    return this.finish(snapId, 'gave_up', `status ${status}`);
  }

  /** A send that never reached the server does not count as a try. */
  private waitForConnection(snapId: string): Outcome {
    this.tries.set(snapId, Math.max(0, (this.tries.get(snapId) ?? 1) - 1));
    // Back off (30 s, 60 s, ... 5 min) so the photo is not shrunk and re-encoded again with every flush while there is no connection.
    const waits = (this.waits.get(snapId) ?? 0) + 1;
    this.waits.set(snapId, waits);
    this.retryAt.set(snapId, this.now() + Math.min(MAX_OFFLINE_WAIT_MS, FIRST_OFFLINE_WAIT_MS * 2 ** (waits - 1)));
    return 'wait';
  }

  /** The upgrade is over for this snap, one way or another: it leaves the queue. Warnings carry a status, never a title or a path. */
  private finish(snapId: string, outcome: Outcome, why?: string): Outcome {
    this.tries.delete(snapId);
    this.waits.delete(snapId);
    this.retryAt.delete(snapId);
    this.deps.store.getState().setShelfUpload(snapId, false);
    if (outcome === 'gave_up' && why) logger.warn(`A shelf photo was not read by the server (${why}).`);
    return outcome;
  }

  /**
   * The server's rows join the cards the way `identify` answers and events do: the rows it added arrive (announced once, as any
   * new row), the phone's rows it skipped follow the one rule for skipped rows (cardsStore `skip`: no card, kept as skipped, the
   * receipt line stays settled, Review can put them back). A row the person has acted on is never touched.
   */
  private merge(rows: CandidateDto[]) {
    const cards = useCardsStore.getState();
    // A skip only applies to a row that still has a card (one the person removed has none and stays gone), never to a discarded
    // one, and never to one the person has acted on. `skip` itself ignores a row with no card.
    const skipped = withoutDiscarded(rows.filter((c) => c.decision === 'rejected')).filter((c) => {
      const mine = cards.byId[c.id];
      return !mine || (mine.decision === 'undecided' && !mine.userEdited && !mine.createdItemId);
    });
    const added = withoutDiscarded(rows.filter((c) => c.decision !== 'rejected'));
    announce(cards.merge(added));
    cards.skip(skipped);
  }
}

