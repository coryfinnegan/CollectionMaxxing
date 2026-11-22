// Where a Review row was found (#833), as pure functions: which photo, which box on it, and how that was known. Then the two views
// the walk draws from the photo: the band (a window onto the still with the item lit) and the strip (the item itself, upright).
import type { BoxDto, CandidateDto } from '@/api/generated/models';
import type { Rect } from '@/native/cm-vision';
import type { Placed, Size } from '@/features/scan/detail/overlay';
import { fitImage } from '@/features/scan/detail/overlay';
import type { PicSnap } from '@/features/scan/pipeline/pipelineStore';
import type { StoredSnap } from '@/features/scan/pipeline/snapStore';

/** `phone`: the phone's own box. `server`: the box the server sent. `unmarked`: the photo is known, the place on it is not. */
export type PlaceHow = 'phone' | 'server' | 'unmarked';

export interface Place {
  snapId: string;
  /** When the photo was taken: photos are walked in this order. */
  takenAt: number;
  /** The stored still, when the phone still has it. */
  still?: { uri: string; width: number; height: number };
  box?: Rect;
  how: PlaceHow;
  /** The phone's stored crop of this item. */
  cropUri?: string;
}

export interface PlaceContext {
  /** The pipeline's receipts: each photo and the card id of each item it sent. */
  snaps: readonly PicSnap[];
  /** The snap store's photos: the still, and each item's box and crop. */
  stored: readonly StoredSnap[];
}

const asRect = (box: BoxDto | null | undefined): Rect | undefined => (box ? { x: box.x, y: box.y, width: box.w, height: box.h } : undefined);

const stillOf = (snap: StoredSnap | undefined) => (snap?.stillUri ? { uri: snap.stillUri, width: snap.width, height: snap.height } : undefined);

/** The photo a row was found in and its box there, or null when no photo is known for it (a barcode, a web session). */
export function placeOf(c: CandidateDto, ctx: PlaceContext): Place | null {
  const find = (snapId: string) => ctx.stored.find((s) => s.snapId === snapId);
  // A phone row: the receipt names its card, or the item carries the row's local id.
  const receipt = ctx.snaps.find((s) => s.items.some((i) => i.candidateId === c.id || (!!c.localId && i.localId === c.localId)));
  if (receipt) {
    const item = receipt.items.find((i) => i.candidateId === c.id || (!!c.localId && i.localId === c.localId))!;
    const snap = find(receipt.snapId);
    const stored = snap?.items.find((i) => i.localId === item.localId);
    return placed(receipt.snapId, receipt.createdAt, snap, stored?.box, asRect(c.box), stored?.cropUri);
  }
  // A server row: its segment names the photo it was read from.
  const segment = c.segmentId ? ctx.snaps.find((s) => s.segmentId === c.segmentId) : undefined;
  if (segment) return placed(segment.snapId, segment.createdAt, find(segment.snapId), undefined, asRect(c.box));
  // The receipt has moved on; the snap store still has the photo for a few days.
  const kept = c.localId ? [...ctx.stored].reverse().find((s) => s.items.some((i) => i.localId === c.localId)) : undefined;
  if (kept) {
    const stored = kept.items.find((i) => i.localId === c.localId);
    return placed(kept.snapId, kept.createdAt, kept, stored?.box, asRect(c.box), stored?.cropUri);
  }
  return null;
}

function placed(snapId: string, takenAt: number, snap: StoredSnap | undefined, phoneBox: Rect | undefined, serverBox: Rect | undefined, cropUri?: string): Place {
  const box = phoneBox ?? serverBox;
  return {
    snapId,
    takenAt,
    ...(stillOf(snap) ? { still: stillOf(snap) } : {}),
    ...(box ? { box } : {}),
    how: phoneBox ? 'phone' : serverBox ? 'server' : 'unmarked',
    ...(cropUri ? { cropUri } : {}),
  };
}

/** The line under a photo whose place on it is not known. */
export const UNMARKED_LINE = "Found in this photo. The server didn't mark where.";

/** A box taller than 2.5 times its width is a spine: it is turned to read. A wide, short box (a flat stack, a cover) is not. */
export function isTall(box: Rect, still: Size): boolean {
  return box.height * still.height > 2.5 * box.width * still.width;
}

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));
const round = (n: number) => Math.round(n * 100) / 100;

/** The band's window: never narrower than this share of the photo. */
export const MIN_WINDOW = 0.35;
/** The lit item and about one neighbor each side: the window is this many box widths wide. */
const NEIGHBORS = 3.2;

export interface BandView {
  /** The still, in band points (it overflows the band; the band clips it). */
  image: Placed;
  /** The lit box, in band points. */
  box?: Placed;
}

/**
 * The band's view of the still: zoomed so the box and about one neighbor each side fill the band's width (never narrower than
 * 35% of the photo), centered on the box and kept inside the photo. With no box, the whole photo, contained.
 */
export function bandView(still: Size, box: Rect | undefined, band: Size): BandView {
  if (!box || band.width <= 0 || band.height <= 0 || still.width <= 0 || still.height <= 0) return { image: fitImage(band, still) };
  const window = clamp(NEIGHBORS * box.width, MIN_WINDOW, 1) * still.width;
  const scale = band.width / window;
  const width = still.width * scale;
  const height = still.height * scale;
  const cx = (box.x + box.width / 2) * width;
  const cy = (box.y + box.height / 2) * height;
  const left = width >= band.width ? clamp(band.width / 2 - cx, band.width - width, 0) : (band.width - width) / 2;
  const top = height >= band.height ? clamp(band.height / 2 - cy, band.height - height, 0) : (band.height - height) / 2;
  return {
    image: { left: round(left), top: round(top), width: round(width), height: round(height) },
    box: { left: round(left + box.x * width), top: round(top + box.y * height), width: round(box.width * width), height: round(box.height * height) },
  };
}

export interface StripView {
  /** The strip's own size. */
  outer: Size;
  /** The clipping view, before it is turned, centered in the strip. */
  clip: Placed;
  /** The still inside the clip, so only the box shows. */
  image: Placed;
  /** True when the clip is turned a quarter left so a spine's title reads. */
  turned: boolean;
}

/** The item clipped out of the still and turned upright when it is a spine, as large as fits `max`. */
export function stripView(still: Size, box: Rect, max: Size): StripView | null {
  const bw = box.width * still.width;
  const bh = box.height * still.height;
  if (bw <= 0 || bh <= 0 || max.width <= 0 || max.height <= 0) return null;
  const turned = isTall(box, still);
  const scale = turned ? Math.min(max.width / bh, max.height / bw) : Math.min(max.width / bw, max.height / bh);
  const clipW = bw * scale;
  const clipH = bh * scale;
  const outer = turned ? { width: round(clipH), height: round(clipW) } : { width: round(clipW), height: round(clipH) };
  return {
    outer,
    clip: { left: round((outer.width - clipW) / 2), top: round((outer.height - clipH) / 2), width: round(clipW), height: round(clipH) },
    image: { left: round(-box.x * still.width * scale), top: round(-box.y * still.height * scale), width: round(still.width * scale), height: round(still.height * scale) },
    turned,
  };
}
