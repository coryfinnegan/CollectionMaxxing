// Test setup for the result detail: a session with a few cards, and the on-phone snap (still, boxes, crops) they were read from.
import type { CandidateDto, IdentifyItem } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { photoMock } from '@/mocks/scan-photo';
import { scanMock } from '@/mocks/scan';
import type { Rect } from '@/native/cm-vision';
import { DEV_CODES } from '../scanner/devCodes';
import { useCardsStore } from '../cards/cardsStore';
import { createPipelineStore, setPipelineStore, type PicItem } from '../pipeline/pipelineStore';
import { MemorySnapFiles, SnapStore, setSnapStore } from '../pipeline/snapStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { setCropReader, useCoverStore } from './coverPhoto';

export const STILL_BYTES = 900_000;
export const CROP_BYTES = 5_000;

export interface SeedItem {
  code: string;
  localId: string;
  box?: Rect;
  withCrop?: boolean;
  text?: string;
}

export const BOX_A: Rect = { x: 0.1, y: 0.2, width: 0.2, height: 0.5 };
export const BOX_B: Rect = { x: 0.5, y: 0.1, width: 0.25, height: 0.6 };

/** Opens a session, resolves one card per item, and records the snap they came from. Returns the cards in order. */
export async function seedSnap(
  items: SeedItem[],
  { snapId = 'snap-1', still = true, width = 2000, height = 1500 }: { snapId?: string; still?: boolean; width?: number; height?: number } = {},
) {
  const tray = createTrayStore({ storage: new MemoryKeyValueStore() });
  setTrayStore(tray);
  const res = await fetch(`http://api.test/api/v1/libraries/${MOCK_LIBRARY_ID}/scan-sessions`, { method: 'POST', body: '{}' });
  const { id: sessionId } = (await res.json()) as { id: string };
  tray.getState().setSessionId(sessionId);
  const batch = await fetch(`http://api.test/api/v1/scan-sessions/${sessionId}/barcodes`, {
    method: 'POST',
    body: JSON.stringify({ codes: items.map((i) => ({ value: i.code, symbology: 'ean-13', copies: 1 })) }),
  });
  const { candidates } = (await batch.json()) as { candidates: CandidateDto[] };
  const cards = candidates.map((c, n) => ({ ...c, localId: items[n]!.localId, visibleText: items[n]!.text ?? c.release?.title ?? null }));
  useCardsStore.getState().merge(cards, { announce: false });

  const files = new MemorySnapFiles();
  const snaps = new SnapStore({ files, storage: new MemoryKeyValueStore() });
  setSnapStore(snaps);
  if (still) await snaps.saveStill(snapId, { uri: 'file:///tmp/still.jpg', width, height });
  for (const item of items) {
    if (!still) break;
    await snaps.saveItem(snapId, { localId: item.localId, text: item.text ?? item.localId, box: item.box }, item.withCrop === false ? undefined : `file:///tmp/crop-${item.localId}.jpg`);
  }

  const pipeline = createPipelineStore({ storage: new MemoryKeyValueStore() });
  setPipelineStore(pipeline);
  const picItems: PicItem[] = items.map((item, n) => ({
    localId: item.localId,
    text: item.text ?? item.localId,
    title: item.text ?? item.localId,
    payload: { localId: item.localId } as unknown as IdentifyItem,
    candidateId: cards[n]!.id,
  }));
  pipeline.getState().addSnap({
    snapId,
    trigger: 'shutter',
    createdAt: Date.now(),
    thumbUri: 'file:///tmp/thumb.jpg',
    phase: 'read',
    sent: true,
    sessionId,
    items: picItems,
    unreadableCount: 0,
  });

  // The phone reads a stored file as bytes: the still is big, a crop is small.
  setCropReader(async (uri) => new Blob([new Uint8Array(uri.includes('still') ? STILL_BYTES : CROP_BYTES)], { type: 'image/jpeg' }));
  return { sessionId, cards, snaps, files };
}

export const TWO = [
  { code: DEV_CODES.mario64New, localId: 'local-mario', box: BOX_A },
  { code: DEV_CODES.goldeneyeOwned, localId: 'local-goldeneye', box: BOX_B },
] satisfies SeedItem[];

export function resetDetail() {
  scanMock.reset();
  photoMock.reset();
  useCardsStore.getState().reset();
  useCoverStore.getState().reset();
  setTrayStore(undefined);
  setPipelineStore(undefined);
  setSnapStore(undefined);
  setCropReader(undefined);
}
