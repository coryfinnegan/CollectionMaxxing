// Which picture a card came from and which other cards came from the same one. The pipeline's receipt knows the card id of each
// item it sent; the snap store holds the still, each item's box and its crop. Both stay on the phone and are only read here.
import { useMemo } from 'react';
import { useStore } from 'zustand';
import type { BoxDto, CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import { useCardsStore } from '../cards/cardsStore';
import { getPipelineStore } from '../pipeline/pipelineStore';
import { getSnapStore, type StoredItem, type StoredSnap } from '../pipeline/snapStore';

export interface SnapPeer {
  card: CandidateDto;
  /** The phone's own record of this item: its box and crop. */
  stored?: StoredItem;
}

export interface SnapContext {
  snapId: string;
  /** The stored still, when the phone still has it. */
  snap?: StoredSnap;
  /** The cards from this picture, in the order they were read. */
  peers: SnapPeer[];
}

const asRect = (box: BoxDto | null | undefined): Rect | undefined => (box ? { x: box.x, y: box.y, width: box.w, height: box.h } : undefined);

/** The box to draw for a card: the phone's own, else the one the server echoed. */
export const boxOf = (peer: SnapPeer): Rect | undefined => peer.stored?.box ?? asRect(peer.card.box);

/** The picture a card belongs to, or undefined for a card that was not read from a picture (a barcode). */
export function useSnapContext(cardId: string): SnapContext | undefined {
  const snaps = useStore(getPipelineStore(), (s) => s.snaps);
  const byId = useCardsStore((s) => s.byId);
  return useMemo(() => {
    const card = byId[cardId];
    if (!card) return undefined;
    const cardFor = (item: { localId: string; candidateId?: string }) =>
      (item.candidateId ? byId[item.candidateId] : undefined) ?? (card.localId === item.localId ? card : undefined);
    const receipt = snaps.find((s) => s.items.some((i) => i.candidateId === cardId || (!i.candidateId && !!card.localId && i.localId === card.localId)));
    const store = getSnapStore();
    if (receipt) {
      const snap = store.get(receipt.snapId);
      const peers = receipt.items.flatMap((item): SnapPeer[] => {
        const found = cardFor(item);
        return found ? [{ card: found, stored: snap?.items.find((i) => i.localId === item.localId) }] : [];
      });
      return { snapId: receipt.snapId, snap, peers };
    }
    // The receipt has moved on (the scan was added); the snap store still has the picture for a few days.
    const snap = card.localId ? [...store.list()].reverse().find((s) => s.items.some((i) => i.localId === card.localId)) : undefined;
    if (!snap) return undefined;
    const peers = snap.items.flatMap((stored): SnapPeer[] => {
      const found = Object.values(byId).find((c) => c.localId === stored.localId);
      return found ? [{ card: found, stored }] : [];
    });
    return { snapId: snap.snapId, snap, peers };
  }, [snaps, byId, cardId]);
}
