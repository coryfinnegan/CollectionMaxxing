// The client end of the recognition pipeline (docs/02 "Per snap"). A snap's still is read on the phone, its items appear on
// the receipt as they are read, and one `identify` call per snap sends only their text and fields. The picture, the crops
// and the boxes stay on the phone. A close call has its candidates' artwork compared here and only the distances go back.
// Offline, the read still happens and the call waits; the read results are saved, so a killed app loses nothing.
//
// Scanning never adds anything: no item is created and no candidate is accepted here. Add, Wishlist and Review do that.
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import { ApiError } from '@/lib/http';
import { LOCAL_PREFIX, useCardsStore } from '../cards/cardsStore';
import type { SnapEvent } from '../autosnap/useAutoSnap';
import { discardPicture, discardPicturesOf, trackPicture } from '../pictureFiles';
import { announce, flushDeps, rejectCandidate } from '../session/sessionApi';
import type { TrayStore } from '../tray/trayStore';
import type { ReadItem, SnapReading, Still } from '../recognition/types';
import { compareArtwork, type ArtDeps, type ItemPicture } from './artStep';
import { identifyRequestOf, picItemOf } from './payload';
import type { PipelineApi } from './pipelineApi';
import type { PicItem, PicSnap, PipelineStore } from './pipelineStore';
import type { SnapStore } from './snapStore';

/** Makes the small picture of one item from its box on the still, and returns where it is. Undefined when it cannot. */
export interface CropMaker {
  thumb(still: Still, box: Rect): Promise<string | undefined>;
}

export interface Reader {
  read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading>;
}

export interface PipelineDeps {
  libraryId: string;
  tray: StoreApi<TrayStore>;
  store: StoreApi<PipelineStore>;
  snaps: SnapStore;
  recognition: Reader;
  api: PipelineApi;
  crops: CropMaker;
  art: ArtDeps;
  /** CAM-3e: a release id when this crop is something the person has confirmed before. Leave it out and sending never waits for a crop. */
  localMatch?: (crop: { uri: string }) => Promise<string | undefined>;
  newId?: () => string;
  now?: () => number;
}

const randomId = () =>
  'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });

/** Snaps being read right now, across screens, so a screen that opens again does not read the same snap twice. */
const reading = new Set<string>();

/** How long to wait after a 429 when the server did not say, and the limits on what it says. */
const DEFAULT_WAIT_MS = 5_000;
const MIN_WAIT_MS = 1_000;
const MAX_WAIT_MS = 2 * 60_000;

const SLOW_DOWN = 'The server asked us to slow down. Your pictures are saved and will finish in a moment.';
const TOO_BIG = 'A picture had too many items to look up at once. Take it again from a bit closer.';
const CANNOT_SEND = "Couldn't look up a picture, so nothing was added from it. Take it again.";

const isSnapInUse = (error: ApiError) => error.problem.code === 'scan.snap_in_use' || (error.problem.type?.endsWith('scan.snap_in_use') ?? false);

const boxKey = (snapId: string, localId: string) => `${snapId}:${localId}`;
const sameBox = (a: Rect | undefined, b: Rect | undefined) => JSON.stringify(a) === JSON.stringify(b);

export class SnapPipeline {
  private barcodeInView: () => boolean = () => false;
  private onOutcome: (found: boolean) => void = () => undefined;
  private flushing: Promise<void> | undefined;
  private readonly crops = new Map<string, { box: Rect | undefined; task: Promise<string | undefined> }>();
  private readonly artTried = new Set<string>();
  private artChain: Promise<void> = Promise.resolve();
  private unsubscribe: (() => void) | undefined;
  /** After a 429: nothing is sent before this time (ms since the epoch). */
  private holdUntil = 0;

  constructor(private readonly deps: PipelineDeps) {}

  /** A barcode that can be read right now wins over a picture. */
  setBarcodeProbe(probe: () => boolean) {
    this.barcodeInView = probe;
  }

  /** Told after each read whether it found anything, so repeated misfires raise auto-snap's thresholds. */
  setOutcomeHandler(handler: (found: boolean) => void) {
    this.onOutcome = handler;
  }

  /** Starts watching for close calls and picks up snaps a killed app left half-read. */
  start() {
    this.stop();
    this.unsubscribe = useCardsStore.subscribe(() => this.scanForCloseCalls());
    this.scanForCloseCalls();
    // Every snap still here has unresolved work: pin it before trimming, so retention never takes it.
    for (const snap of this.deps.store.getState().snaps) {
      if (snap.libraryId === this.deps.libraryId) this.deps.snaps.pin(snap.snapId);
    }
    void this.deps.snaps.prune();
    this.resume();
  }

  stop() {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
  }

  /** One picture from the shutter or auto-snap. Resolves once it is read and sent (or queued). */
  async onSnap(event: SnapEvent, options: { recognition?: Reader } = {}): Promise<void> {
    // A barcode in view wins: the barcode path reads it, no picture is sent. An imported picture is not what the camera sees.
    if (event.trigger !== 'import' && this.barcodeInView()) return;
    const { store, snaps } = this.deps;
    const snapId = (this.deps.newId ?? randomId)();
    const photoUri = trackPicture(event.photo.uri, snapId); // the scaled snap: deleted once the store has its own copy
    store.getState().addSnap({
      snapId,
      libraryId: this.deps.libraryId,
      trigger: event.trigger,
      shotKind: event.shotKind,
      segmentId: (this.deps.newId ?? randomId)(),
      createdAt: this.now(),
      thumbUri: event.photo.uri,
      phase: 'reading',
      sent: false,
      items: [],
      unreadableCount: 0,
    });
    reading.add(snapId);
    snaps.pin(snapId); // unresolved from here until it is dropped, swept or sent and settled
    const stored = await snaps.saveStill(snapId, event.photo);
    if (stored) {
      store.getState().setSnapThumb(snapId, stored.stillUri); // the receipt line shows the store's copy, not the file about to go
      await discardPicture(photoUri);
      if (!store.getState().snaps.some((s) => s.snapId === snapId)) void this.drop(snapId); // cleared while it was being kept
    }
    const still: Still = stored
      ? { uri: stored.stillUri, width: stored.width, height: stored.height }
      : { uri: event.photo.uri, width: event.photo.width, height: event.photo.height };
    await this.read(snapId, still, options.recognition ?? this.deps.recognition);
    await this.flush();
  }

  private async read(snapId: string, still: Still, recognition: Reader): Promise<void> {
    const { store } = this.deps;
    reading.add(snapId);
    try {
      const result = await recognition.read(still, (item) => {
        store.getState().upsertItem(snapId, picItemOf(item));
        this.makeCrop(snapId, still, item);
      });
      const items = result.items.map(picItemOf);
      store.getState().finishRead(snapId, {
        items,
        unreadableCount: result.unreadableCount,
        engine: result.engine,
        fellBackFrom: result.fellBackFrom,
      });
      for (const item of result.items) this.makeCrop(snapId, still, item);
      if (items.length === 0) {
        // Nothing found: the line fades out. It costs nothing.
        void this.drop(snapId);
        this.onOutcome(false);
      } else {
        this.onOutcome(true);
      }
    } catch {
      void this.drop(snapId); // a picture that cannot be read is dropped, never a crash
      this.onOutcome(false);
    } finally {
      reading.delete(snapId);
    }
  }

  /** A snap that will not be used: off the receipt, out of the snap store, and none of its picture files left. */
  private async drop(snapId: string) {
    this.deps.store.getState().removeSnap(snapId);
    useCardsStore.getState().dropLocal(snapId);
    this.deps.snaps.unpin(snapId);
    await Promise.all([this.deps.snaps.forget(snapId), discardPicturesOf(snapId)]);
  }

  /** The crop of one item, kept on the phone. The read does not wait for it. */
  private makeCrop(snapId: string, still: Still, item: ReadItem) {
    const key = boxKey(snapId, item.localId);
    if (this.crops.get(key) && sameBox(this.crops.get(key)?.box, item.box)) return;
    const task = (async () => {
      const made = item.box ? await this.deps.crops.thumb(still, item.box).catch(() => undefined) : undefined;
      const cropUri = made ? trackPicture(made, snapId) : undefined;
      const saved = await this.deps.snaps.saveItem(snapId, { localId: item.localId, text: item.text, box: item.box }, cropUri);
      // The store keeps its own copy of the crop, so the one that was made is deleted; with no copy it is the only one and stays
      // for the receipt, unless the snap is gone already.
      if (cropUri && (saved?.cropUri || !this.deps.snaps.get(snapId))) await discardPicture(cropUri);
      const uri = saved?.cropUri ?? (cropUri && !this.deps.snaps.get(snapId) ? undefined : cropUri);
      if (uri) this.deps.store.getState().setThumb(snapId, item.localId, uri);
      return uri;
    })().catch(() => undefined);
    this.crops.set(key, { box: item.box, task });
  }

  /** Re-reads snaps the app was killed in the middle of, from the still the snap store kept. */
  private resume() {
    for (const snap of this.deps.store.getState().snaps) {
      if (snap.libraryId !== this.deps.libraryId || snap.phase !== 'reading' || reading.has(snap.snapId)) continue;
      const stored = this.deps.snaps.get(snap.snapId);
      if (!stored) {
        // The still is gone too (the OS emptied the cache): count it, never silently complete it.
        this.deps.store.getState().noteOrphaned();
        void this.drop(snap.snapId);
        continue;
      }
      const still: Still = { uri: stored.stillUri, width: stored.width, height: stored.height };
      reading.add(snap.snapId);
      void this.read(snap.snapId, still, this.deps.recognition).then(() => this.flush());
    }
  }

  /** Sends every read snap that has not been sent, one `identify` per snap, oldest first. */
  flush(): Promise<void> {
    if (this.now() < this.holdUntil) return Promise.resolve(); // the server asked us to slow down (429)
    this.flushing ??= this.sendWaiting().finally(() => {
      this.flushing = undefined;
    });
    return this.flushing;
  }

  private now() {
    return (this.deps.now ?? Date.now)();
  }

  private async sendWaiting() {
    const { tray } = this.deps;
    const refused: string[] = [];
    try {
      await this.sendEach(refused);
    } finally {
      // Said once the send is over, so a snap that did go through after it cannot wipe what the person needs to know.
      const message = refused.at(-1);
      if (message) tray.getState().setOnline(tray.getState().online, message);
    }
  }

  private async sendEach(refused: string[]) {
    const { store, tray, api } = this.deps;
    const classify = flushDeps(this.deps.libraryId).classify;
    let resets = 0;
    const renamed = new Set<string>();
    for (;;) {
      // Barcodes first: identify waits while the tray still holds codes the server has not confirmed. The tray's own
      // flush sends them; the next pipeline flush tries again. A tray with a problem of its own (a refused batch, a
      // signed-out user) does not hold pictures up: only waiting work waits. Art-scores chain off identify's answers
      // and crops stay local, so the reconnect order is barcodes, then identify in shoot order, then art, then crops (§9).
      const unsentCodes = tray.getState().lines.some((l) => l.sentCopies < l.copies);
      if (unsentCodes && !tray.getState().problem) return;
      const next = store
        .getState()
        .snaps.filter((s) => s.libraryId === this.deps.libraryId && s.phase === 'read' && !s.sent)
        .sort((a, b) => a.createdAt - b.createdAt)[0];
      if (!next) return;
      if (this.barcodeInView()) return; // held until the barcode is out of view
      try {
        await this.findLocalMatches(next);
        const snap = store.getState().snaps.find((s) => s.snapId === next.snapId);
        if (!snap) continue; // removed while waiting
        let sessionId = tray.getState().sessionId;
        if (!sessionId) {
          sessionId = await api.createSession(this.deps.libraryId);
          tray.getState().setSessionId(sessionId);
        }
        const candidates = await api.identify(sessionId, identifyRequestOf(snap));
        const byLocalId: Record<string, string> = {};
        for (const c of candidates) if (c.localId) byLocalId[c.localId] = c.id;
        store.getState().markSent(snap.snapId, sessionId, byLocalId);
        announce(useCardsStore.getState().merge(candidates));
        tray.getState().setOnline(true);
      } catch (error) {
        const status = error instanceof ApiError ? error.status : undefined;
        if (error instanceof ApiError && status === 429) {
          // Too many requests (docs/04): keep the snap, wait as long as the server said, then send it again unchanged.
          const wait = Math.min(MAX_WAIT_MS, Math.max(MIN_WAIT_MS, error.retryAfterMs ?? DEFAULT_WAIT_MS));
          this.holdUntil = this.now() + wait;
          tray.getState().setOnline(true, SLOW_DOWN);
          return;
        }
        if (error instanceof ApiError && status === 409 && isSnapInUse(error) && !renamed.has(next.snapId)) {
          // That id is held by another session. This picture has not been sent to this one, so it goes under a new id.
          renamed.add(next.snapId);
          store.getState().setSendId(next.snapId, (this.deps.newId ?? randomId)());
          continue;
        }
        const outcome = classify(error);
        if (outcome.offline) {
          tray.getState().setOnline(false); // the lines show QUEUED and the next flush tries again with the same snapId
          return;
        }
        if (outcome.resetSession) {
          // The scan session is gone. Its rows still hold the snapIds of what was sent to it, so every snap still waiting is
          // sent to the new session under a new id.
          tray.getState().setSessionId(null);
          tray.getState().setOnline(true);
          for (const waiting of store.getState().snaps) {
            if (waiting.libraryId === this.deps.libraryId && waiting.phase === 'read' && !waiting.sent)
              store.getState().setSendId(waiting.snapId, (this.deps.newId ?? randomId)());
          }
          if (resets++ > 0) return; // twice in one send: the next flush tries again
          continue;
        }
        if (status === 401) {
          tray.getState().setOnline(true, outcome.message); // signed out: the read waits; signing in again sends it
          return;
        }
        // The server refused this one for good (400, 413 and the like); trying again would only refuse again. The person is told.
        refused.push(status === 413 ? TOO_BIG : CANNOT_SEND);
        await this.drop(next.snapId);
      }
    }
  }

  /**
   * CAM-3e's hook: an item the person has confirmed before is sent with its release, and drawn as a local card at once. Sending
   * waits only as long as the crops take.
   */
  private async findLocalMatches(snap: PicSnap) {
    const match = this.deps.localMatch;
    if (!match) return;
    for (const item of snap.items) {
      if (item.payload.localMatchReleaseId) continue;
      const uri = await this.crops.get(boxKey(snap.snapId, item.localId))?.task;
      if (!uri) continue;
      const releaseId = await match({ uri }).catch(() => undefined);
      if (!releaseId) continue;
      this.deps.store.getState().setPayload(snap.snapId, item.localId, { ...item.payload, localMatchReleaseId: releaseId });
      this.showLocalCard(snap, item, releaseId);
    }
  }

  /**
   * A repeat of something the person confirmed before gets its card at once, from the match on the phone, offline too. It is a
   * display-only card until the server answers; the server's word replaces it (and verifies the release it was sent with).
   */
  private showLocalCard(snap: PicSnap, item: PicItem, releaseId: string) {
    const cards = useCardsStore.getState();
    const id = `${LOCAL_PREFIX}${snap.snapId}:${item.localId}`;
    if (cards.local[id] || Object.values(cards.byId).some((c) => c.localId === item.localId)) return;
    cards.showLocal({
      id,
      seq: 0,
      source: 'picture_apple_model',
      rawCode: null,
      symbology: null,
      visibleText: item.title || item.text,
      localId: item.localId,
      box: null,
      boxSource: null,
      resolution: 'matched',
      score: 1,
      artDistance: null,
      needsArt: false,
      release: { id: releaseId, title: item.title || item.text },
      inferred: null,
      alternatives: null,
      decision: 'undecided',
      copies: 1,
      alreadyOwnedCount: 0,
      ownedAt: null,
      createdItemId: null,
      changeSeq: 0,
      edits: null,
      userEdited: false,
      pendingReason: null,
      retryAfter: null,
    } as unknown as CandidateDto);
  }

  /** The scan this snap was sent to is over: its lines leave the receipt. */
  sweep() {
    const { store, tray } = this.deps;
    const before = store.getState().snaps.map((s) => s.snapId);
    store.getState().sweep(tray.getState().sessionId, this.deps.libraryId);
    const after = new Set(store.getState().snaps.map((s) => s.snapId));
    for (const snapId of before) {
      if (after.has(snapId)) continue;
      useCardsStore.getState().dropLocal(snapId);
      this.deps.snaps.unpin(snapId); // the scan is over: retention may take the still again
      void discardPicturesOf(snapId);
    } // the snap store keeps its own copies for its retention
  }

  /** Swipe to remove a picture item: gone from the receipt and the cards, and rejected on the server so Review will not add it. */
  removeItem(snapId: string, localId: string) {
    const { store, tray } = this.deps;
    const item = store.getState().snaps.find((s) => s.snapId === snapId)?.items.find((i) => i.localId === localId);
    store.getState().removeItem(snapId, localId);
    if (!store.getState().snaps.some((s) => s.snapId === snapId)) this.deps.snaps.unpin(snapId);
    useCardsStore.getState().dropLocal(snapId, localId);
    const card = item && this.cardOf(item);
    if (card) {
      useCardsStore.getState().remove(card.id);
      const sessionId = tray.getState().sessionId;
      if (sessionId) void rejectCandidate(sessionId, card.id).catch(() => undefined);
    }
  }

  private cardOf(item: PicItem): CandidateDto | undefined {
    const { byId } = useCardsStore.getState();
    return (item.candidateId ? byId[item.candidateId] : undefined) ?? Object.values(byId).find((c) => c.localId === item.localId);
  }

  // ---- close calls ----------------------------------------------------------------------------------------------

  private scanForCloseCalls() {
    for (const card of Object.values(useCardsStore.getState().byId)) {
      if (!card.needsArt || this.artTried.has(card.id) || !this.itemFor(card)) continue;
      this.artTried.add(card.id); // once: a failure leaves the card as it is
      this.artChain = this.artChain.then(() => this.compareAndReport(card.id)).catch(() => undefined);
    }
  }

  private itemFor(card: CandidateDto): { snap: PicSnap; item: PicItem } | undefined {
    for (const snap of this.deps.store.getState().snaps) {
      if (snap.libraryId !== this.deps.libraryId) continue; // the same title scanned in another library is another picture
      const item = snap.items.find((i) => i.candidateId === card.id || (!!card.localId && i.localId === card.localId));
      if (item) return { snap, item };
    }
    return undefined;
  }

  private async pictureOf(snap: PicSnap, item: PicItem): Promise<ItemPicture | undefined> {
    await this.crops.get(boxKey(snap.snapId, item.localId))?.task;
    const stored = this.deps.snaps.get(snap.snapId);
    const kept = stored?.items.find((i) => i.localId === item.localId);
    if (kept?.cropUri) return { uri: kept.cropUri };
    if (stored && kept?.box) return { uri: stored.stillUri, box: kept.box };
    if (stored && snap.items.length === 1) return { uri: stored.stillUri }; // a single item: the whole picture is the item
    return undefined;
  }

  private async compareAndReport(candidateId: string) {
    const sessionId = this.deps.tray.getState().sessionId;
    const card = useCardsStore.getState().byId[candidateId];
    if (!sessionId || !card?.needsArt) return;
    const found = this.itemFor(card);
    if (!found) return;
    const picture = await this.pictureOf(found.snap, found.item);
    if (!picture) return;
    const distances = await compareArtwork(this.deps.art, card, picture);
    if (!distances) return;
    try {
      const updated = await this.deps.api.scoreArt(sessionId, candidateId, distances);
      if (updated) announce(useCardsStore.getState().merge([updated]));
    } catch {
      // leave the card as it was: Pick one
    }
  }
}
