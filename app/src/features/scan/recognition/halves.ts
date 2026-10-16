// A wide shot with many items is read in two halves, then merged. Pure functions; the selector does the reading.
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning } from './defaults';
import { centerX, fromRegion, right } from './geometry';
import { normalize } from './lines';
import type { ReadItem } from './types';

const MIDDLE = 1 / 2;

/** Where to cut: the widest stretch with no text across it, near the middle. Falls back to the middle itself. */
export function chooseCut(lines: TextLine[], tuning: LocalTuning): number {
  const spans = lines
    .map((l) => [l.box.x, right(l.box)] as const)
    .sort((a, b) => a[0] - b[0]);
  let best = { cut: MIDDLE, gap: 0 };
  let reach = 0;
  for (const [start, end] of spans) {
    const gap = start - reach;
    const cut = reach + gap / 2;
    if (gap > best.gap && Math.abs(cut - MIDDLE) <= tuning.cutSearchRadius) best = { cut, gap };
    reach = Math.max(reach, end);
  }
  return best.cut;
}

/** Each half reaches a little past the cut so an item on the cut is whole in at least one of them. */
export function halfRegions(cut: number, tuning: LocalTuning): [Rect, Rect] {
  const left = Math.min(1, cut + tuning.halfOverlap);
  const start = Math.max(0, cut - tuning.halfOverlap);
  return [
    { x: 0, y: 0, width: left, height: 1 },
    { x: start, y: 0, width: 1 - start, height: 1 },
  ];
}

const sameItem = (a: ReadItem, b: ReadItem) => normalize(a.title) === normalize(b.title);

/** Puts both halves back on the whole picture, drops what the overlap read twice and renumbers left to right. */
export function mergeHalves(
  left: { items: ReadItem[]; region: Rect },
  right_: { items: ReadItem[]; region: Rect },
  tuning: LocalTuning,
): ReadItem[] {
  const place = (items: ReadItem[], region: Rect) =>
    items.map((i): ReadItem => (i.box ? { ...i, box: fromRegion(i.box, region) } : { ...i }));
  const a = place(left.items, left.region);
  const b = place(right_.items, right_.region);

  const extra = b.filter((candidate) => {
    const twin = a.find((existing) => {
      if (!sameItem(existing, candidate)) return false;
      if (!existing.box || !candidate.box) return true;
      return Math.abs(centerX(existing.box) - centerX(candidate.box)) <= tuning.seamTolerance;
    });
    return !twin;
  });

  const all = [...a, ...extra];
  const position = (i: ReadItem) => (i.box ? centerX(i.box) : Number.POSITIVE_INFINITY);
  const ordered = all
    .map((item, index) => ({ item, index }))
    .sort((p, q) => (position(p.item) === position(q.item) ? p.index - q.index : position(p.item) - position(q.item)));
  return ordered.map(({ item }, order) => ({ ...item, order }));
}
