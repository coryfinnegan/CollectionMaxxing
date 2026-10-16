// Splitting what Vision saw into items, with no model: rectangles first, then spine columns, else the whole frame.
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning, RecognitionConfig } from './defaults';
import { bottom, centerX, centerY, contains, right, union } from './geometry';

export interface Piece {
  lines: TextLine[];
  box: Rect;
}

export interface Split {
  pieces: Piece[];
  /** Rectangles or columns that held no text. */
  unreadableCount: number;
}

const readingOrder = (a: TextLine, b: TextLine) => a.box.y - b.box.y || a.box.x - b.box.x;

function piece(lines: TextLine[], box?: Rect): Piece {
  const ordered = [...lines].sort(readingOrder);
  return { lines: ordered, box: box ?? union(ordered.map((l) => l.box)) };
}

function byRectangles(lines: TextLine[], rectangles: Rect[]): Split {
  const sorted = [...rectangles].sort((a, b) => a.x - b.x || a.y - b.y);
  const pieces: Piece[] = [];
  let unreadableCount = 0;
  for (const rect of sorted) {
    const inside = lines.filter((l) => contains(rect, centerX(l.box), centerY(l.box)));
    if (inside.length === 0) unreadableCount += 1;
    else pieces.push(piece(inside, rect));
  }
  return { pieces, unreadableCount };
}

function byColumns(lines: TextLine[], tuning: LocalTuning, minWidth: number): Split | undefined {
  const columns: { box: Rect; lines: TextLine[] }[] = [];
  for (const l of [...lines].sort((a, b) => a.box.x - b.box.x)) {
    const last = columns[columns.length - 1];
    const shared = last ? Math.min(right(last.box), right(l.box)) - Math.max(last.box.x, l.box.x) : 0;
    if (last && shared >= l.box.width * tuning.spineColumnOverlap) {
      last.lines.push(l);
      last.box = union([last.box, l.box]);
    } else {
      columns.push({ box: l.box, lines: [l] });
    }
  }
  const wide = columns.filter((c) => c.box.width >= minWidth);
  if (wide.length < tuning.minSpineColumns) return undefined;
  return { pieces: wide.map((c) => piece(c.lines)), unreadableCount: 0 };
}

const isTall = (l: TextLine) => bottom(l.box) - l.box.y > l.box.width;

export function splitIntoPieces(
  lines: TextLine[],
  rectangles: Rect[],
  splitting: RecognitionConfig['splitting'],
  tuning: LocalTuning,
): Split {
  if (lines.length === 0) return { pieces: [], unreadableCount: 0 };
  if (rectangles.length > 0) {
    const split = byRectangles(lines, rectangles);
    if (split.pieces.length > 0) return split;
  }
  const tallShare = lines.filter(isTall).length / lines.length;
  if (tallShare >= tuning.spineTallShare) {
    const split = byColumns(lines, tuning, splitting.minSpineColumnWidth);
    if (split) return split;
  }
  return { pieces: [piece(lines)], unreadableCount: 0 };
}
