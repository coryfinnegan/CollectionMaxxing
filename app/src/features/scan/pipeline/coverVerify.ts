// Stage 2 (#438): the phone posts the cover still and the detected cover bounds to :verify-cover,
// and the server compares the capture against the alternatives' own covers. Only the ranked answer
// comes back. Anything the phone cannot send — no still, no isolatable cover, an oversize capture —
// or anything the server refuses (409) resolves to undefined, and the caller leaves the card as it is.
import type { CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import { fitLongEdge, type Resizer } from '../autosnap/snapPhoto';
import type { PipelineApi } from './pipelineApi';
import type { SnapStore } from './snapStore';

/** The server takes captures up to 4 MB (JpegSanitizer.MaxBytes); check here so a too-big file is never sent. */
export const MAX_VERIFY_BYTES = 4 * 1024 * 1024;

/** Captures shrink to this long edge: the server scales to 1024 px before SIFT, so this keeps detail with room to spare. */
export const VERIFY_LONG_EDGE = 1600;

/** Below this the corners are a line, not a cover (mirrors the server's CoverQuad.MinArea). */
const MIN_QUAD_AREA = 1e-6;

const clamp01 = (n: number) => Math.min(1, Math.max(0, n));

/**
 * The item box as the quad the endpoint reads (`{"topLeft":{"x","y"},…}`, origin top-left, 0–1).
 * Undefined when the box is not a real area inside the picture.
 */
export function quadJsonOf(box: Rect): string | undefined {
  const { x, y, width, height } = box;
  if (![x, y, width, height].every((n) => typeof n === 'number' && Number.isFinite(n))) return undefined;
  if (width <= 0 || height <= 0) return undefined;
  const x1 = clamp01(x);
  const y1 = clamp01(y);
  const x2 = clamp01(x + width);
  const y2 = clamp01(y + height);
  if ((x2 - x1) * (y2 - y1) <= MIN_QUAD_AREA) return undefined;
  return JSON.stringify({
    topLeft: { x: x1, y: y1 },
    topRight: { x: x2, y: y1 },
    bottomRight: { x: x2, y: y2 },
    bottomLeft: { x: x1, y: y2 },
  });
}

/** The still to send, with the cover quad when the item's bounds isolate it. */
export interface CoverCapture {
  stillUri: string;
  width: number;
  height: number;
  quad?: string;
}

/**
 * The still and quad for one item, from the snap store. A lone item without usable bounds still
 * sends: the whole picture is its cover. A boxless item among several, or a missing still, sends
 * nothing — the server could not know which cover to compare.
 */
export function coverCaptureOf(
  snaps: Pick<SnapStore, 'get'>,
  snap: { snapId: string; items: readonly unknown[] },
  item: { localId: string },
): CoverCapture | undefined {
  const stored = snaps.get(snap.snapId);
  if (!stored) return undefined;
  const still = { stillUri: stored.stillUri, width: stored.width, height: stored.height };
  const box = stored.items.find((i) => i.localId === item.localId)?.box;
  const quad = box ? quadJsonOf(box) : undefined;
  if (quad) return { ...still, quad };
  return snap.items.length === 1 ? still : undefined;
}

export interface CoverVerifyDeps {
  /** Reads a stored still as a file to send. */
  readFile(uri: string): Promise<Blob>;
  /** Shrinks an oversized still; the same resizer the shutter uses. */
  shrink: Resizer;
  /** Deletes a shrunk copy once it has been sent (or skipped). */
  discard(uri: string): Promise<void>;
}

/**
 * Posts one ambiguous close call's cover to :verify-cover. Resolves to the re-ranked candidate,
 * or undefined when there is nothing to compare, the capture is too big, or the post fails. A
 * shrunk copy is always deleted; the stored still is never touched.
 */
export async function verifyCoverPhoto(
  deps: CoverVerifyDeps,
  api: Pick<PipelineApi, 'verifyCover'>,
  sessionId: string,
  card: Pick<CandidateDto, 'id' | 'alternatives'>,
  capture: CoverCapture,
): Promise<CandidateDto | undefined> {
  if (!Array.isArray(card.alternatives) || card.alternatives.length === 0) return undefined;
  let uri = capture.stillUri;
  try {
    const target = fitLongEdge(capture.width, capture.height, VERIFY_LONG_EDGE);
    if (target) {
      const shrunk = await deps.shrink({ uri, width: capture.width, height: capture.height, orientation: 1 }, target);
      uri = shrunk.uri;
    }
    const file = await deps.readFile(uri);
    if (file.size > MAX_VERIFY_BYTES) return undefined;
    return await api.verifyCover(sessionId, card.id, file, capture.quad);
  } catch {
    return undefined;
  } finally {
    // A shrink that answered with the still itself leaves it alone.
    if (uri !== capture.stillUri) await deps.discard(uri).catch(() => undefined);
  }
}
