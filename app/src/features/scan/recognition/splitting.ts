// Splitting what Vision saw into items, with no model: rectangles first, then spine columns (found from the tall lines), else the whole frame.
// Spine columns also split a rectangle that holds several spines, and read the lines that sit outside every rectangle (#509).
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning, RecognitionConfig } from './defaults';
import { bottom, centerX, centerY, contains, overlapShare, right, union, yOverlapShare } from './geometry';

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

/** Rectangles that largely overlap depict one surface (a cover's nested quads); disjoint spine quads pass through. */
function mergeSurfaces(rectangles: Rect[], minOverlap: number): Rect[] {
  const groups: Rect[][] = [];
  for (const rect of [...rectangles].sort((a, b) => a.x - b.x || a.y - b.y)) {
    const group = groups.find((g) => g.some((r) => overlapShare(r, rect) >= minOverlap));
    if (group) group.push(rect);
    else groups.push([rect]);
  }
  return groups.map(union);
}

/** One item per rectangle, or one per spine when the text inside forms spine columns in one row. `outside` holds the lines in no rectangle. */
function byRectangles(lines: TextLine[], rectangles: Rect[], tuning: LocalTuning, minWidth: number): Split & { outside: TextLine[] } {
  const sorted = mergeSurfaces(rectangles, tuning.rectangleMergeOverlap);
  const pieces: Piece[] = [];
  const taken = new Set<TextLine>();
  let unreadableCount = 0;
  for (const rect of sorted) {
    const inside = lines.filter((l) => contains(rect, centerX(l.box), centerY(l.box)));
    inside.forEach((l) => taken.add(l));
    if (inside.length === 0) unreadableCount += 1;
    else pieces.push(...(bySpineColumns(inside, tuning, minWidth)?.pieces ?? [piece(inside, rect)]));
  }
  return { pieces, unreadableCount, outside: lines.filter((l) => !taken.has(l)) };
}

const isTall = (l: TextLine) => bottom(l.box) - l.box.y > l.box.width;

interface Column {
  box: Rect;
  lines: TextLine[];
}

/** Tall lines left to right: a line joins the last column when it shares enough of its own width with it. */
function columnsOf(tallLines: TextLine[], tuning: LocalTuning): Column[] {
  const columns: Column[] = [];
  for (const l of [...tallLines].sort((a, b) => a.box.x - b.box.x)) {
    const last = columns[columns.length - 1];
    const shared = last ? Math.min(right(last.box), right(l.box)) - Math.max(last.box.x, l.box.x) : 0;
    if (last && shared >= l.box.width * tuning.spineColumnOverlap) {
      last.lines.push(l);
      last.box = union([last.box, l.box]);
    } else {
      columns.push({ box: l.box, lines: [l] });
    }
  }
  return columns;
}

/**
 * Spines stand in one row: most neighbors share their vertical band, so one short book beside tall ones does not undo the split.
 * Scattered cover lines share almost none, so they are not split (#504).
 */
function inOneRow(columns: Column[], tuning: LocalTuning): boolean {
  const pairs = columns.length - 1;
  const sharing = columns.filter((c, i) => i > 0 && yOverlapShare(columns[i - 1].box, c.box) >= tuning.spineRowOverlap).length;
  return sharing / pairs >= tuning.spineRowShare;
}

/**
 * Spine columns come from the tall lines alone, so books lying flat in the frame do not hide the spines. A short line whose center
 * sits across a column inside the row's band joins that column; every other line (a flat stack below the row) becomes one more piece.
 * A tall line in a column too narrow to be a spine is treated like a short line, so no line is dropped.
 */
function bySpineColumns(lines: TextLine[], tuning: LocalTuning, minWidth: number): Split | undefined {
  const tallColumns = columnsOf(lines.filter(isTall), tuning);
  const columns = tallColumns.filter((c) => c.box.width >= minWidth);
  if (columns.length < tuning.minSpineColumns || !inOneRow(columns, tuning)) return undefined;
  const band = union(columns.map((c) => c.box));
  const narrow = tallColumns.filter((c) => !columns.includes(c)).flatMap((c) => c.lines);
  const rest: TextLine[] = [];
  for (const l of [...lines.filter((l) => !isTall(l)), ...narrow]) {
    const x = centerX(l.box);
    const across = centerY(l.box) >= band.y && centerY(l.box) <= bottom(band) ? columns.filter((c) => x >= c.box.x && x <= right(c.box)) : [];
    if (across.length === 0) rest.push(l);
    else across.reduce((a, b) => (Math.abs(x - centerX(b.box)) < Math.abs(x - centerX(a.box)) ? b : a)).lines.push(l);
  }
  const pieces = columns.map((c) => piece(c.lines));
  if (rest.length > 0) pieces.push(piece(rest));
  return { pieces, unreadableCount: 0 };
}

/** One item for the whole frame: the cover path, never segmented. Empty when there is no text. */
export function wholeFrame(lines: TextLine[]): Split {
  if (lines.length === 0) return { pieces: [], unreadableCount: 0 };
  return { pieces: [piece(lines)], unreadableCount: 0 };
}

export function splitIntoPieces(
  lines: TextLine[],
  rectangles: Rect[],
  splitting: RecognitionConfig['splitting'],
  tuning: LocalTuning,
): Split {
  if (lines.length === 0) return { pieces: [], unreadableCount: 0 };
  const withoutRectangles = (rest: TextLine[]) => bySpineColumns(rest, tuning, splitting.minSpineColumnWidth) ?? wholeFrame(rest);
  if (rectangles.length > 0) {
    const { outside, ...split } = byRectangles(lines, rectangles, tuning, splitting.minSpineColumnWidth);
    // Vision may box only some of the books: the lines outside every rectangle still become items.
    if (split.pieces.length > 0) return { ...split, pieces: [...split.pieces, ...withoutRectangles(outside).pieces] };
  }
  return withoutRectangles(lines);
}
