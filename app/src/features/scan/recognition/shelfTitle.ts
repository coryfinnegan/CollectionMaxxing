// The title of a stretch of shelf, one upright spine mostly. A spine prints more than its title, and Vision reads the same
// spine several times, damaged differently each time, so the title is settled in four steps (#617): of several readings of
// one place only the one the others agree with stays; a sticker and a line that names the author's standing go, whatever
// size they are printed; of what is left only the biggest lettering can be the title, which is what keeps the author's
// name out of it; and a title printed over two rows of one spine is read as one title.
//
// The author's name is kept out by its lettering and not by reading like a name: creators.ts's name test, which may guess
// wrong cheaply, matches most shelf titles too ("THEY LIVE", "Funny Feelings", "THE LUCKIEST LADY IN LONDON"), and asking
// it here cost 5 of the 58 titles the recorded shelf photos hold. A name printed as big as the title beside it is left to
// the server, which weighs a read's creator against each candidate's.
import type { Rect, TextLine } from '@/native/cm-vision';
import { readsAsRole } from './creators';
import type { LocalTuning } from './defaults';
import { bottom, overlapShare, right, yOverlapShare } from './geometry';
import { wordsOf } from './lines';

/** Words that name a sticker and never an item: an edition, a format, a shelf code. A line of nothing else is a sticker. */
const STICKER_WORDS = new Set([
  'widescreen', 'fullscreen', 'screen', 'wide', 'full', 'special', 'collector', 'collectors', 'limited', 'deluxe',
  'anniversary', 'edition', 'editions', 'remastered', 'digitally', 'unrated', 'rated', 'director', 'directors', 'cut',
  'stock', 'no', 'vol', 'disc', 'side', 'sealed', 'used', 'price', 'sale', 'closed', 'captioned', 'ntsc', 'pal',
]);

export interface ShelfTitle {
  /** The line the title was read from; the author-finder must not take that line for the author. */
  line: TextLine;
  /** Its text, with the second row of a two-row title read as part of it. */
  text: string;
}

/** A line down a spine is read sideways, so its letters are as tall as its box is wide; a line across one reads upright. */
const isSideways = (l: TextLine) => bottom(l.box) - l.box.y > l.box.width;
const letters = (l: TextLine) => (isSideways(l) ? l.box.width : l.box.height);
const area = (r: Rect) => r.width * r.height;
const sameSize = (a: number, b: number, share: number) => Math.min(a, b) >= Math.max(a, b) * share;

/** Shared horizontal span over the narrower box's width: the mirror of `yOverlapShare`, for a title lying flat. */
function xOverlapShare(a: Rect, b: Rect): number {
  const w = Math.min(right(a), right(b)) - Math.max(a.x, b.x);
  return w <= 0 ? 0 : w / Math.min(a.width, b.width);
}

/**
 * Whether the line says nothing but numbers and a sticker's words: a stock number, "WIDESCREEN", "10th ANNIVERSARY
 * EDITION", "COLLECTOR'S". A word of one letter says nothing either way, so it neither makes a sticker nor saves a line
 * from being one. A word with a number in it is a code or a count, never the whole of a title.
 */
export function readsAsSticker(text: string): boolean {
  const words = wordsOf(text).filter((w) => w.length > 1);
  return words.length === 0 || words.every((w) => /\d/.test(w) || STICKER_WORDS.has(w));
}

const jaccard = (a: Set<string>, b: Set<string>): number => {
  const shared = [...a].filter((w) => b.has(w)).length;
  const union = a.size + b.size - shared;
  return union === 0 ? 0 : shared / union;
};

/** How much of its own words another reading also read; a reading is not rewarded for the words only it saw. */
const sharedShare = (mine: Set<string>, theirs: Set<string>) => [...mine].filter((w) => theirs.has(w)).length / mine.size;

/** The surest, longest reading: what the title was chosen by before agreement had a say. */
const weight = (l: TextLine) => l.confidence * l.text.length;
const heaviest = (lines: TextLine[]) => lines.reduce((a, b) => (weight(b) > weight(a) ? b : a));

/** Readings of one place and one text: the boxes are one box and most of the words they name together are in both. */
class Readings {
  private readonly words: Map<TextLine, Set<string>>;

  constructor(
    lines: TextLine[],
    private readonly tuning: LocalTuning,
  ) {
    this.words = new Map(lines.map((l) => [l, new Set(wordsOf(l.text))]));
  }

  private said(l: TextLine): Set<string> {
    return this.words.get(l) ?? new Set();
  }

  /**
   * One place, not one box inside another: the two share this much of their union. So the author's name, which sits inside a
   * reading of the title and the author at once, is a place of its own and not another reading of the title.
   */
  samePlace(a: TextLine, b: TextLine): boolean {
    const shared = overlapShare(a.box, b.box) * Math.min(area(a.box), area(b.box));
    return shared / (area(a.box) + area(b.box) - shared) >= this.tuning.sameBoxOverlap;
  }

  sameText(a: TextLine, b: TextLine): boolean {
    return jaccard(this.said(a), this.said(b)) >= this.tuning.sameReadingWords;
  }

  /** The other readings of this line's place and text. */
  of(l: TextLine, pool: TextLine[]): TextLine[] {
    return pool.filter((k) => k !== l && this.samePlace(k, l) && this.sameText(k, l));
  }

  /**
   * How much the other readings of this line's place agree with it: the share of its own words they also read, averaged, so
   * a long reading of two rows at once does not beat a clean reading of the title. A line nobody else read scores nothing.
   */
  agreement(l: TextLine, pool: TextLine[]): number {
    const others = this.of(l, pool);
    if (others.length === 0) return 0;
    return others.reduce((sum, k) => sum + sharedShare(this.said(l), this.said(k)), 0) / others.length;
  }

  /** The better of two readings: the one the others agree with more, else the surer and longer. */
  better(a: TextLine, b: TextLine, pool: TextLine[]): TextLine {
    const mine = this.agreement(a, pool);
    const theirs = this.agreement(b, pool);
    return theirs > mine || (theirs === mine && weight(b) > weight(a)) ? b : a;
  }

  /**
   * The second row of a title printed over two rows of one spine: the same lettering, beside the chosen row along the whole
   * of it and within a letter's gap, in another place, saying other words. Another reading of the chosen row is neither.
   */
  secondRow(chosen: TextLine, pool: TextLine[]): TextLine | undefined {
    const sideways = isSideways(chosen);
    const size = letters(chosen);
    const beside = (k: TextLine) => {
      if (isSideways(k) !== sideways || this.samePlace(chosen, k) || this.sameText(chosen, k)) return false;
      if (overlapShare(chosen.box, k.box) >= this.tuning.titleRowOverlap) return false;
      if (!sameSize(letters(k), size, this.tuning.titleLetters)) return false;
      const along = sideways ? yOverlapShare(chosen.box, k.box) : xOverlapShare(chosen.box, k.box);
      const gap = sideways
        ? Math.max(chosen.box.x, k.box.x) - Math.min(right(chosen.box), right(k.box))
        : Math.max(chosen.box.y, k.box.y) - Math.min(bottom(chosen.box), bottom(k.box));
      return along >= this.tuning.titleRowOverlap && gap <= size * this.tuning.titleRowMaxGap;
    };
    // Of two rows beside the chosen one, the surer, longer reading is the real second row.
    const rows = pool.filter((k) => k !== chosen && beside(k));
    return rows.length === 0 ? undefined : heaviest(rows);
  }
}

/** A spine prints its title top to bottom, so its rows run right to left in the picture; a label lying flat reads downwards. */
function inRowOrder(chosen: TextLine, second: TextLine): TextLine[] {
  const secondIsFirst = isSideways(chosen) ? second.box.x > chosen.box.x : second.box.y < chosen.box.y;
  return secondIsFirst ? [second, chosen] : [chosen, second];
}

/** Drops what cannot be a title, unless the piece held nothing else. */
const without = (lines: TextLine[], cannot: (l: TextLine) => boolean): TextLine[] => {
  const left = lines.filter((l) => !cannot(l));
  return left.length > 0 ? left : lines;
};

/** The piece's title, or nothing when the piece holds no line at all. */
export function shelfTitleOf(lines: TextLine[], tuning: LocalTuning): ShelfTitle | undefined {
  if (lines.length === 0) return undefined;
  const readings = new Readings(lines, tuning);
  // One reading per place: the reading the other readings of that place agree with, so the worst of three does not win.
  const agreeing = lines.filter((l) => readings.of(l, lines).every((k) => readings.better(l, k, lines) === l));
  // A sticker and a line that names the author's standing are never an item's title, whatever size they are printed.
  const said = without(agreeing, (l) => readsAsSticker(l.text) || readsAsRole(l.text));
  // Only the biggest lettering left can be the title. An author's name is printed smaller than the title above it, so this
  // is what keeps it out; a shelf label and a stock number are smaller still.
  const biggest = Math.max(...said.map(letters));
  const big = said.filter((l) => sameSize(letters(l), biggest, tuning.titleLetters));
  const chosen = big.reduce((best, l) => readings.better(best, l, lines));
  // The second row is looked for among every line the piece could print, not only the biggest: a title's second row is
  // measured against the row chosen, not against the biggest lettering a damaged reading of the spine happens to claim.
  const second = readings.secondRow(chosen, said);
  const text = (second ? inRowOrder(chosen, second) : [chosen]).map((l) => l.text.trim()).join(' ');
  return { line: chosen, text };
}
