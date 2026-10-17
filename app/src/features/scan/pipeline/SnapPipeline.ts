// The client end of the recognition pipeline (docs/02 "Per snap"). A snap's still is read on the phone, its items appear on
// the receipt as they are read, and one `identify` call per snap sends only their text and fields. The picture, the crops
// and the boxes stay on the phone. A close call has its candidates' artwork compared here and only the distances go back.
// Offline, the read still happens and the call waits; the read results are saved, so a killed app loses nothing.
//
// Scanning never adds anything: no item is created and no candidate is accepted here. Add, Wishlist and Review do that.
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import { useCardsStore } from '../cards/cardsStore';
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
    void this.deps.snaps.prune();
    this.resume();
  }

  stop() {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
  }

  /** One picture from the shutter or auto-snap. Resolves once it is read and sent (or queued). */
  async onSnap(event: SnapEvent, options: { recognition?: Reader } = {}): Promise<void> {
    if (this.barcodeInView()) return; // a barcode in view wins: the barcode path reads it, no picture is sent
    const { store, snaps } = this.deps;
    const snapId = (this.deps.newId ?? randomId)();
    const photoUri = trackPicture(event.photo.uri, snapId); // the scaled snap: deleted once the store has its own copy
    store.getState().addSnap({
      snapId,
      trigger: event.trigger,
      createdAt: (this.deps.now ?? Date.now)(),
      thumbUri: event.photo.uri,
      phase: 'reading',
      sent: false,
      items: [],
      unreadableCount: 0,
    });
    reading.add(snapId);
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
      if (snap.phase !== 'reading' || reading.has(snap.snapId)) continue;
      const stored = this.deps.snaps.get(snap.snapId);
      if (!stored) {
        this.deps.store.getState().removeSnap(snap.snapId);
        continue;
      }
      const still: Still = { uri: stored.stillUri, width: stored.width, height: stored.height };
      reading.add(snap.snapId);
      void this.read(snap.snapId, still, this.deps.recognition).then(() => this.flush());
    }
  }

  /** Sends every read snap that has not been sent, one `identify` per snap, oldest first. */
  flush(): Promise<void> {
    this.flushing ??= this.sendWaiting().finally(() => {
      this.flushing = undefined;
    });
    return this.flushing;
  }

  private async sendWaiting() {
    const { store, tray, api } = this.deps;
    const classify = flushDeps(this.deps.libraryId).classify;
    for (;;) {
      const next = store.getState().snaps.find((s) => s.phase === 'read' && !s.sent);
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
        const outcome = classify(error);
        if (outcome.offline) {
          tray.getState().setOnline(false); // the lines show QUEUED and the next flush tries again with the same snapId
          return;
        }
        tray.getState().setOnline(true, outcome.message);
        if (outcome.resetSession) {
          tray.getState().setSessionId(null);
          return;
        }
        store.getState().removeSnap(next.snapId); // the server refused this one; trying again would only refuse again
      }
    }
  }

  /**
   * CAM-3e's hook: an item the person has confirmed before is sent with its release. Nothing is wired today (`localMatch.ts`
   * returns nothing), so sending never waits for a crop; once it is wired, it waits only as long as the crops take.
   */
  private async findLocalMatches(snap: PicSnap) {
    const match = this.deps.localMatch;
    if (!match) return;
    for (const item of snap.items) {
      if (item.payload.localMatchReleaseId) continue;
      const uri = await this.crops.get(boxKey(snap.snapId, item.localId))?.task;
      if (!uri) continue;
      const releaseId = await match({ uri }).catch(() => undefined);
      if (releaseId) this.deps.store.getState().setPayload(snap.snapId, item.localId, { ...item.payload, localMatchReleaseId: releaseId });
    }
  }

  /** The scan this snap was sent to is over: its lines leave the receipt. */
  sweep() {
    const { store, tray } = this.deps;
    const before = store.getState().snaps.map((s) => s.snapId);
    store.getState().sweep(tray.getState().sessionId);
    const after = new Set(store.getState().snaps.map((s) => s.snapId));
    for (const snapId of before) if (!after.has(snapId)) void discardPicturesOf(snapId); // the snap store keeps its own copies for its retention
  }

  /** Swipe to remove a picture item: gone from the receipt and the cards, and rejected on the server so Review will not add it. */
  removeItem(snapId: string, localId: string) {
    const { store, tray } = this.deps;
    const item = store.getState().snaps.find((s) => s.snapId === snapId)?.items.find((i) => i.localId === localId);
    store.getState().removeItem(snapId, localId);
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
