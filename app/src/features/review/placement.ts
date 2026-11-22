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

/** The band's height, in points. */
export const BAND_HEIGHT = 220;
/** The lit box is at least this wide in the band, so it is the first thing the eye lands on. */
const MIN_BOX = 48;
/** About three neighbors each side: the box takes at least this share of the band's width. */
const MIN_SHARE = 1 / 7;
/** A wide flat stack or a cover is never zoomed past this share of the band's width. */
const MAX_SHARE = 0.6;
/** How close the drawn outline comes to the band's edge. */
const EDGE = 4;

export interface BandView {
  /** The still, in band points (it overflows the band; the band clips it). */
  image: Placed;
  /** The outline to draw, in band points: the lit box, or the part of it the band shows, inset from the band's edges. */
  box?: Placed;
}

/**
 * The band's view of the still. Zoomed in so the lit box is at least 48 pt wide and at least a seventh of the band (about three
 * neighbors each side), never past 60% of the band (a flat stack, a cover). The still always covers the whole band: no bars. The
 * window is centered on the box and kept on the photo; a box at the photo's own edge moves the window just enough that its whole
 * outline shows. A box taller (or wider) than the band is cropped, and its outline is drawn round the part that shows, 4 pt in
 * from the band's edges, so all four edges always show. With no box, the whole photo, contained.
 */
export function bandView(still: Size, box: Rect | undefined, band: Size): BandView {
  if (!box || band.width <= 0 || band.height <= 0 || still.width <= 0 || still.height <= 0) return { image: fitImage(band, still) };
  const boxPx = Math.max(1e-9, box.width * still.width);
  const zoomIn = Math.max(MIN_BOX / boxPx, (band.width * MIN_SHARE) / boxPx);
  const cover = Math.max(band.width / still.width, band.height / still.height);
  const scale = Math.max(cover, Math.min(zoomIn, (band.width * MAX_SHARE) / boxPx));
  const width = still.width * scale;
  const height = still.height * scale;
  /** Centered on the box and kept on the photo; when the whole box fits the band, it also keeps EDGE of room round it. */
  const offset = (start: number, size: number, imageSize: number, bandSize: number) => {
    const onPhoto = clamp(bandSize / 2 - (start + size / 2), bandSize - imageSize, 0);
    return size + 2 * EDGE <= bandSize ? clamp(onPhoto, EDGE - start, bandSize - EDGE - (start + size)) : onPhoto;
  };
  const left = offset(box.x * width, box.width * width, width, band.width);
  const top = offset(box.y * height, box.height * height, height, band.height);
  const x0 = Math.max(EDGE, left + box.x * width);
  const y0 = Math.max(EDGE, top + box.y * height);
  const x1 = Math.min(band.width - EDGE, left + (box.x + box.width) * width);
  const y1 = Math.min(band.height - EDGE, top + (box.y + box.height) * height);
  return {
    image: { left: round(left), top: round(top), width: round(width), height: round(height) },
    box: { left: round(x0), top: round(y0), width: round(Math.max(0, x1 - x0)), height: round(Math.max(0, y1 - y0)) },
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
