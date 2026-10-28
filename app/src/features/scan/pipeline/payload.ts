// What goes to /identify: JSON text and fields only. A box, a crop or a path never appears in a request.
import type { FallbackReason, IdentifyItem, IdentifyRequest, ScanEngine } from '@/api/generated/models';
import { toIdentifyItem } from '../recognition/mapping';
import type { EngineName, FellBackFrom, ReadItem } from '../recognition/types';
import type { PicItem, PicSnap } from './pipelineStore';

/** The server knows four reasons; a timeout is its own, so stats can tell a slow model from an outage. */
export function toFallbackReason(from: FellBackFrom): FallbackReason {
  return from === 'contextExceeded' ? 'context' : from;
}

/** The phone's engine names are the server's engine names. */
export function toScanEngine(engine: EngineName): ScanEngine {
  return engine;
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
    snapId: snap.sendId ?? snap.snapId,
    trigger: snap.trigger,
    shotKind: snap.shotKind ?? 'unknown',
    engine: toScanEngine(snap.engine ?? 'vision_pipeline'),
    ...(snap.fellBackFrom ? { fellBackFrom: toFallbackReason(snap.fellBackFrom) } : {}),
    ...(snap.boundCandidateId ? { kind: 'cover' as const, candidateId: snap.boundCandidateId } : {}),
    items: snap.items.map((i) => {
      // The snap's segment links a multi-item snap; an item stamped with its own keeps it.
      const segmentId = i.payload.segmentId ?? snap.segmentId;
      return segmentId === undefined ? i.payload : { ...i.payload, segmentId };
    }),
  };
}
