// What goes to /identify: JSON text and fields only. A box, a crop or a path never appears in a request.
import type { FallbackReason, IdentifyItem, IdentifyRequest } from '@/api/generated/models';
import { toIdentifyItem } from '../recognition/mapping';
import type { FellBackFrom, ReadItem } from '../recognition/types';
import type { PicItem, PicSnap } from './pipelineStore';

/** The server knows three reasons; a timeout is a read that produced nothing usable, so it counts as unavailable. */
export function toFallbackReason(from: FellBackFrom): FallbackReason {
  return from === 'contextExceeded' ? 'context' : from === 'timeout' ? 'unavailable' : from;
}

/** `IdentifyItem` plus the format the engines read (a VHS tape is a film in the VHS format). */
export function identifyItemOf(item: ReadItem): IdentifyItem {
  const out = toIdentifyItem(item);
  if (item.format) out.format = item.format;
  return out;
}

export function picItemOf(item: ReadItem): PicItem {
  return { localId: item.localId, text: item.text, title: item.title, payload: identifyItemOf(item) };
}

export function identifyRequestOf(snap: PicSnap): IdentifyRequest {
  return {
    snapId: snap.snapId,
    trigger: snap.trigger,
    engine: snap.engine ?? 'vision_pipeline',
    ...(snap.fellBackFrom ? { fellBackFrom: toFallbackReason(snap.fellBackFrom) } : {}),
    items: snap.items.map((i) => i.payload),
  };
}
