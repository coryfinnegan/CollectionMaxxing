// Several covers lying side by side in one shot: three game boxes on a table, a few paperbacks on a bed. The shelf splitter reads a photo like that
// as one row of spines and finds a blurb; each cover is an item of its own, with its own text, its own title and its own kind.
// Pure: Vision's lines and rectangles in, the covers and the lines that belong to none out. The numbers are `MultiCoverTuning` in defaults.ts.
import type { Rect, TextLine } from '@/native/cm-vision';
import { hugsFrame } from './coverFormat';
import { coverKind, type CueKind } from './coverKind';
import { coverTitleOf, type CoverTitle, type PictureSize } from './coverTitle';
import {
  DEFAULT_COVER_FORMAT_TUNING,
  DEFAULT_MULTI_COVER_TUNING,
  type CoverFormatTuning,
  type LocalTuning,
  type MultiCoverTuning,
} from './defaults';
import { centerX, centerY, contains, overlapShare } from './geometry';

/** One cover: the lines inside its rectangle, the rectangle, and what its own text and its neighbors say it is. */
export interface Cover {
  lines: TextLine[];
  /** The cover's rectangle: where the item is on the picture. */
  box: Rect;
  /** The biggest text on the cover; nothing when no line is left to be one (the caller then picks as it always did). */
  title?: CoverTitle;
  kind?: CueKind;
}

export interface MultipleCovers {
  /** Left to right, then top to bottom. */
  covers: Cover[];
  /** The lines inside no cover: the spine splitter reads these as it always did. */
  rest: TextLine[];
}

type FrameTuning = Pick<CoverFormatTuning, 'frameEdgeMargin' | 'frameHuggingSides'>;

const positive = (n: number) => Number.isFinite(n) && n > 0;
const area = (r: Rect) => r.width * r.height;

/** A rectangle's short side over its long side, in pixels (Vision's rectangles are 0..1 of each side, so a 0.5 by 0.5 one on a 3:4 picture is not a square). */
function aspectOf(r: Rect, picture: PictureSize): number {
  const sides = [r.width * (picture.width > 0 ? picture.width : 1), r.height * (picture.height > 0 ? picture.height : 1)];
  return Math.min(...sides) / Math.max(...sides);
}

/** The rectangles shaped and sized like a cover, with the nested and the heavily overlapping ones dropped: of two that overlap, the bigger stands. */
function coverShaped(rectangles: Rect[], picture: PictureSize, tuning: MultiCoverTuning, frame: FrameTuning): Rect[] {
  const fitting = rectangles.filter((r) => {
    const aspect = aspectOf(r, picture);
    return positive(r.width) && positive(r.height) && !hugsFrame(r, frame) && area(r) >= tuning.minCoverage && aspect >= tuning.minCoverAspect && aspect <= tuning.maxCoverAspect;
  });
  const kept: Rect[] = [];
  for (const r of [...fitting].sort((a, b) => area(b) - area(a) || a.x - b.x || a.y - b.y)) {
    if (!kept.some((k) => overlapShare(k, r) > tuning.maxOverlap)) kept.push(r);
  }
  return kept;
}

/** Whether a line stands upright on the picture (taller than wide, in pixels): the text on a spine does, the text on a cover seldom. */
const isUpright = (l: TextLine, picture: PictureSize) => l.box.height * (picture.height > 0 ? picture.height : 1) > l.box.width * (picture.width > 0 ? picture.width : 1);

/**
 * The kind each cover's own text names. A cover that names none takes the kind the others name, when at least `kindAgreeingCovers` of them
 * name it and none names another.
 */
function kindsOf(covers: { lines: TextLine[] }[], tuning: MultiCoverTuning): (CueKind | undefined)[] {
  const own = covers.map((c) => coverKind(c.lines));
  const named = own.filter((k): k is CueKind => k !== undefined);
  const agreed = new Set(named).size === 1 && named.length >= tuning.kindAgreeingCovers ? named[0] : undefined;
  return own.map((k) => k ?? agreed);
}

/**
 * The covers in a photo of several, or nothing when it is not one: the shot is then read as it always was.
 *
 * It is several covers when two or more rectangles are covers and together hold at least `minTextShare` of the lines. A cover is a rectangle at
 * least `minCoverage` of the frame, shaped like a cover (short over long side between `minCoverAspect` and `maxCoverAspect`, in pixels), not the
 * frame's own edge, not overlapping a bigger one much, holding at least `minCoverLines` lines and not mostly upright ones (a block of spines is
 * not a cover). Each cover is one item: its lines are those whose center lies inside it, its title is `coverTitleOf` on them. The lines inside
 * no cover come back as `rest`.
 */
export function multipleCovers(
  lines: TextLine[],
  rectangles: Rect[],
  picture: PictureSize,
  local: LocalTuning,
  tuning: MultiCoverTuning = DEFAULT_MULTI_COVER_TUNING,
  frame: FrameTuning = DEFAULT_COVER_FORMAT_TUNING,
): MultipleCovers | undefined {
  if (lines.length === 0) return undefined;
  const candidates = coverShaped(rectangles, picture, tuning, frame).map((box) => ({ box, lines: [] as TextLine[] }));
  // A line belongs to the smallest rectangle around its center.
  for (const l of lines) {
    const around = candidates.filter((c) => contains(c.box, centerX(l.box), centerY(l.box)));
    if (around.length > 0) around.reduce((a, b) => (area(b.box) < area(a.box) ? b : a)).lines.push(l);
  }
  const held = candidates.filter(
    (c) => c.lines.length > 0 && c.lines.length >= tuning.minCoverLines && c.lines.filter((l) => isUpright(l, picture)).length <= c.lines.length * tuning.maxUprightLineShare,
  );
  const inCovers = new Set(held.flatMap((c) => c.lines));
  if (held.length < tuning.minCovers || inCovers.size < lines.length * tuning.minTextShare) return undefined;

  held.sort((a, b) => a.box.x - b.box.x || a.box.y - b.box.y);
  const kinds = kindsOf(held, tuning);
  const covers = held.map((c, i): Cover => {
    // A game box prints a stylised logo and no author, so its title is read differently (see `MultiCoverTuning.gameTitle`).
    const title = coverTitleOf(c.lines, picture, kinds[i] === 'game' ? { ...local, ...tuning.gameTitle } : local);
    return { lines: c.lines, box: c.box, ...(title ? { title } : {}), ...(kinds[i] ? { kind: kinds[i] } : {}) };
  });
  return { covers, rest: lines.filter((l) => !inCovers.has(l)) };
}
