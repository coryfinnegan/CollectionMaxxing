// Saves a print when a picture candidate is confirmed (docs/02 "Learning loop"): added from its card or ticked at Review, and then
// committed so the item exists. Never on scan alone, never for a rejected or discarded candidate. It watches the shared cards store:
//   1. a card turns accepted: print its crop now, while the snap store still has it (the print is a vector; nothing is written);
//   2. the card leaves the store (Review removes committed cards, a discard clears them): ask the session which items it created;
//      only a candidate with a created item gets its print saved, under that item's id and the candidate's release.
// The crop is the snap store's copy. No picture file is made here, so nothing needs releasing.
import type { StoreApi, UseBoundStore } from 'zustand';
import type { CandidateDto } from '@/api/generated/models';
import type { FeaturePrint } from '@/native/cm-vision';
import type { SnapStore } from '../pipeline/snapStore';
import type { PrintVision } from './matcher';
import type { FingerprintStore } from './store';

export interface RecorderDeps {
  store: FingerprintStore;
  vision: PrintVision;
  snaps: Pick<SnapStore, 'list'>;
  cards: UseBoundStore<StoreApi<{ byId: Record<string, CandidateDto> }>>;
  /** The scan session the cards belong to; null when there is none. */
  sessionId: () => string | null;
  loadSession: (sessionId: string) => Promise<CandidateDto[]>;
  now?: () => Date;
}

interface Pending {
  releaseId: string;
  sessionId: string | null;
  /** The print of the crop, or undefined when there was no crop or it could not be printed. */
  print: Promise<FeaturePrint | undefined>;
}

export class ConfirmationRecorder {
  private readonly pending = new Map<string, Pending>();
  private readonly done = new Set<string>();
  private work: Promise<unknown> = Promise.resolve();
  private stopWatching: (() => void) | undefined;

  constructor(private readonly deps: RecorderDeps) {}

  start(): () => void {
    this.stopWatching?.();
    const watch = (byId: Record<string, CandidateDto>, previous: Record<string, CandidateDto>) => this.onCards(byId, previous);
    this.stopWatching = this.deps.cards.subscribe((state, prev) => watch(state.byId, prev.byId));
    watch(this.deps.cards.getState().byId, {}); // cards already accepted when this starts (a resumed scan)
    return () => this.stop();
  }

  stop() {
    this.stopWatching?.();
    this.stopWatching = undefined;
    this.pending.clear();
  }

  /** Resolves when everything seen so far has been saved or dropped. */
  async settled(): Promise<void> {
    let seen: Promise<unknown>;
    do {
      seen = this.work;
      await seen.catch(() => undefined);
    } while (seen !== this.work);
  }

  private onCards(byId: Record<string, CandidateDto>, previous: Record<string, CandidateDto>) {
    for (const candidate of Object.values(byId)) {
      if (this.done.has(candidate.id)) continue;
      if (candidate.decision === 'accepted' && candidate.release && !this.pending.has(candidate.id)) this.begin(candidate);
      else if (candidate.decision !== 'accepted' && this.pending.has(candidate.id)) this.pending.delete(candidate.id); // un-ticked or skipped
      if (candidate.createdItemId && this.pending.has(candidate.id)) this.finish(candidate.id, candidate.createdItemId);
    }
    for (const id of Object.keys(previous)) {
      if (id in byId || !this.pending.has(id)) continue;
      this.queue(() => this.afterLeaving(id)); // committed, or the scan was discarded: the session knows which
    }
  }

  private queue(task: () => Promise<void>) {
    this.work = this.work.then(task).catch(() => undefined);
  }

  private begin(candidate: CandidateDto) {
    const crop = this.cropOf(candidate.localId);
    const print = crop
      ? this.deps.vision.featurePrint(crop).then(
          (p) => p,
          () => undefined,
        )
      : Promise.resolve(undefined);
    this.pending.set(candidate.id, { releaseId: candidate.release!.id, sessionId: this.deps.sessionId(), print });
  }

  private cropOf(localId: string | null | undefined): string | undefined {
    if (!localId) return undefined;
    for (const snap of this.deps.snaps.list()) {
      const item = snap.items.find((i) => i.localId === localId);
      if (item?.cropUri) return item.cropUri;
    }
    return undefined;
  }

  private async afterLeaving(candidateId: string) {
    const pending = this.pending.get(candidateId);
    if (!pending) return;
    this.pending.delete(candidateId);
    if (!pending.sessionId) return;
    let itemId: string | null | undefined;
    try {
      itemId = (await this.deps.loadSession(pending.sessionId)).find((c) => c.id === candidateId)?.createdItemId;
    } catch {
      return; // offline: the print is skipped, not guessed
    }
    if (itemId) await this.save(candidateId, itemId, pending);
  }

  private finish(candidateId: string, itemId: string) {
    const pending = this.pending.get(candidateId);
    if (!pending) return;
    this.pending.delete(candidateId);
    this.queue(() => this.save(candidateId, itemId, pending));
  }

  private async save(candidateId: string, itemId: string, pending: Pending) {
    const print = await pending.print;
    if (!print) return;
    this.done.add(candidateId);
    this.deps.store.setDeviceRevision(print.revision);
    this.deps.store.put({
      itemId,
      releaseId: pending.releaseId,
      revision: print.revision,
      print: print.data,
      createdAt: (this.deps.now?.() ?? new Date()).toISOString(),
      synced: false,
    });
  }
}
