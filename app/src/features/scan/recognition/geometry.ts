import type { Rect } from '@/native/cm-vision';

export const right = (r: Rect) => r.x + r.width;
export const bottom = (r: Rect) => r.y + r.height;
export const centerX = (r: Rect) => r.x + r.width / 2;
export const centerY = (r: Rect) => r.y + r.height / 2;

export function union(rects: Rect[]): Rect {
  const x = Math.min(...rects.map((r) => r.x));
  const y = Math.min(...rects.map((r) => r.y));
  return { x, y, width: Math.max(...rects.map(right)) - x, height: Math.max(...rects.map(bottom)) - y };
}

/** Intersection area over the smaller box's area; 0 when they do not touch. */
export function overlapShare(a: Rect, b: Rect): number {
  const w = Math.min(right(a), right(b)) - Math.max(a.x, b.x);
  const h = Math.min(bottom(a), bottom(b)) - Math.max(a.y, b.y);
  if (w <= 0 || h <= 0) return 0;
  return (w * h) / Math.min(a.width * a.height, b.width * b.height);
}

/** Shared vertical span over the smaller box's height; 0 when they do not overlap. */
export function yOverlapShare(a: Rect, b: Rect): number {
  const h = Math.min(bottom(a), bottom(b)) - Math.max(a.y, b.y);
  if (h <= 0) return 0;
  return h / Math.min(a.height, b.height);
}

export function contains(outer: Rect, x: number, y: number): boolean {
  return x >= outer.x && x <= right(outer) && y >= outer.y && y <= bottom(outer);
}

/** Maps a box measured inside `region` back onto the whole picture. */
export function fromRegion(box: Rect, region: Rect): Rect {
  return {
    x: region.x + box.x * region.width,
    y: region.y + box.y * region.height,
    width: box.width * region.width,
    height: box.height * region.height,
  };
}
