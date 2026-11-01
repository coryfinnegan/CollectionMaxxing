// The title of a stretch of shelf, one upright spine mostly. A spine prints more than its title, and Vision reads the same
// spine several times, damaged differently each time, so the title is settled in four steps (#617): of several readings of
// one place only the one the others agree with stays; a sticker and a line that names the author's standing go, whatever
// size they are printed; of what is left the biggest lettering is the title, which is what keeps the author's name out of
// it; and a title printed over two rows of one spine is read as one title.
//
// The author's name is kept out by its lettering and not by reading like a name: creators.ts's name test, which may guess
// wrong cheaply, matches most shelf titles too ("THEY LIVE", "Funny Feelings", "THE LUCKIEST LADY IN LONDON"), and asking
// it here cost 5 of the 58 titles the recorded shelf photos hold. A name printed as big as the title beside it is left to
// the server, which weighs a read's creator against each candidate's.
import type { Rect, TextLine } from '@/native/cm-vision';
import { readsAsRole } from './creators';
import type { LocalTuning } from './defaults';
import { bottom, overlapShare, right } from './geometry';
import { wordsOf } from './lines';

/**
 * Words that mark a sticker: an edition, a format, a shelf code. Nearly every one of them is also a title on its own
 * ("Special", "Cut", "Used", "Side"), so one of these alone is still read as a title.
 */
const STICKER_WORDS = new Set([
  'screen', 'wide', 'full', 'special', 'limited', 'deluxe', 'anniversary', 'edition', 'editions', 'rated', 'director',
  'directors', 'cut', 'stock', 'no', 'vol', 'disc', 'side', 'sealed', 'used', 'price', 'sale', 'closed', 'captioned',
]);

/** Words that are a sticker on their own: no item is called any of these. */
const STICKER_ALONE = new Set(['widescreen', 'fullscreen', 'collector', 'collectors', 'remastered', 'digitally', 'unrated', 'ntsc', 'pal']);

/** A number an item prints: a count, a year, an ordinal ("10th"). It says nothing either way about a sticker. */
const isNumber = (word: string) => /^\d+(st|nd|rd|th)?$/.test(word);

export interface ShelfTitle {
  /** The line the title was read from; the author-finder must not take that line for the author. */
  line: TextLine;
  /** Its text, with the second row of a two-row title read as part of it. */
  text: string;
  /** The lines left over that could name the author: not the title's own rows, and no sticker. */
  rest: TextLine[];
}

/** A line down a spine is read sideways, so its letters are as tall as its box is wide; a line across one reads upright. */
const isSideways = (l: TextLine) => bottom(l.box) - l.box.y > l.box.width;
const letters = (l: TextLine) => (isSideways(l) ? l.box.width : l.box.height);
const area = (r: Rect) => r.width * r.height;
const sameSize = (a: number, b: number, share: number) => Math.min(a, b) >= Math.max(a, b) * share;

/**
 * How much of the longer row the two rows share along the line: down the picture for an upright spine, across it for a label
 * lying flat. Measured against the longer row, so a short line that happens to sit inside a long one does not read as a row
 * running beside it for its whole length.
 */
function alongShare(a: Rect, b: Rect, sideways: boolean): number {
  const span = sideways
    ? Math.min(bottom(a), bottom(b)) - Math.max(a.y, b.y)
    : Math.min(right(a), right(b)) - Math.max(a.x, b.x);
  const longest = sideways ? Math.max(a.height, b.height) : Math.max(a.width, b.width);
  return span <= 0 ? 0 : span / longest;
}

/**
 * Whether the line says nothing but a sticker: every word is a number or one of a sticker's words, and at least one of them
 * is a word and not a number. "WIDESCREEN", "Stock No. 038" and "10th ANNIVERSARY EDITION" are stickers; "1984", "SE7EN"
 * and "GREMLINS2" are titles. One word on its own is a sticker only when no item is called that, or when it is a run of
 * digits too long to be a year or a count. A single letter says nothing: it neither makes a sticker nor saves a line from
 * being one, so "COLLECTOR'S" is a sticker while "Vol 2", whose 2 is a number and does count, is one too.
 */
export function readsAsSticker(text: string, tuning: LocalTuning): boolean {
  const words = wordsOf(text).filter((w) => w.length > 1 || isNumber(w));
  if (words.length === 0) return true;
  if (words.length === 1) return STICKER_ALONE.has(words[0]) || (isNumber(words[0]) && words[0].length >= tuning.stickerCodeDigits);
  return words.every((w) => isNumber(w) || STICKER_WORDS.has(w) || STICKER_ALONE.has(w)) && words.some((w) => !isNumber(w));
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

  /**
   * The better of two lines as a title: the one the other readings of its place agree with more; then the one printed in
   * bigger lettering, since an author's name, a bestseller line and a sticker all sit under a title set larger than they
   * are; and only then the surer, longer reading, which is all the choice went by before (#617).
   */
  better(a: TextLine, b: TextLine, pool: TextLine[]): TextLine {
    const keys = (l: TextLine) => [this.agreement(l, pool), letters(l), weight(l)];
    const [mine, theirs] = [keys(a), keys(b)];
    const first = mine.findIndex((k, i) => k !== theirs[i]);
    return first >= 0 && theirs[first] > mine[first] ? b : a;
  }

  /**
   * The second row of a title printed over two rows of one spine: the same lettering, beside the chosen row along the whole
   * of it and touching it, in another place, saying other words. Another reading of the chosen row is none of those.
   */
  secondRow(chosen: TextLine, pool: TextLine[]): TextLine | undefined {
    const sideways = isSideways(chosen);
    const size = letters(chosen);
    const beside = (k: TextLine) => {
      if (isSideways(k) !== sideways || this.samePlace(chosen, k) || this.sameText(chosen, k)) return false;
      if (!sameSize(letters(k), size, this.tuning.titleRowLetters)) return false;
      // The two rows of one title touch or overlap in the picture. A line that merely sits near the chosen row is the next
      // spine along, an award line or a publisher, and joining one of those would say two things as one title.
      const gap = sideways
        ? Math.max(chosen.box.x, k.box.x) - Math.min(right(chosen.box), right(k.box))
        : Math.max(chosen.box.y, k.box.y) - Math.min(bottom(chosen.box), bottom(k.box));
      return alongShare(chosen.box, k.box, sideways) >= this.tuning.titleRowOverlap && gap <= 0;
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
  const said = without(agreeing, (l) => readsAsSticker(l.text, tuning) || readsAsRole(l.text));
  const chosen = said.reduce((best, l) => readings.better(best, l, lines));
  const second = readings.secondRow(chosen, said);
  const rows = second ? inRowOrder(chosen, second) : [chosen];
  // The author is looked for among what the title did not use, and never among the stickers: a shelf piece's creator is a
  // guess, but a guess of "WIDESCREEN", or of the title's own second row, costs the server a right title (creators.ts).
  const rest = lines.filter((l) => !rows.includes(l) && !readsAsSticker(l.text, tuning));
  return { line: chosen, text: rows.map((l) => l.text.trim()).join(' '), rest };
}
