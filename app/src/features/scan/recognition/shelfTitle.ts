// The title of a stretch of shelf, one upright spine mostly. A spine prints more than its title, and Vision reads the same
// spine several times, damaged differently each time, so the title is settled like this (#617). A sticker and a line that
// names the author's standing go first, whatever size they are printed. What is left is grouped into places, a place being
// a box and everything read inside it. Two readings of one place differ only in how Vision damaged them, so there the
// clearest reading wins: the one the other readings of its place agree with, else the surest and longest. Two different
// places are two different pieces of print, so there the biggest lettering wins, which is what keeps the author's name,
// the bestseller line and the shelf label out of the title. Last, a title printed over two rows of one spine is read as
// one title.
//
// Comparing lettering only across places is what makes both halves safe. Within a place, lettering says nothing: a damaged
// reading claims a fatter box than the clean reading of the same words, and a reading that caught the title and the author
// in one sweep claims a fatter box than either. Across places, agreement says nothing: an author's name read twice would
// otherwise beat a title read once.
//
// The name is kept out by its lettering and not by reading like a name: creators.ts's name test, which may guess wrong
// cheaply, matches most shelf titles too ("THEY LIVE", "Funny Feelings", "THE LUCKIEST LADY IN LONDON"), and asking it here
// cost 5 of the 58 titles the recorded shelf photos hold. A name printed as big as the title beside it is left to the
// server, which weighs a read's creator against each candidate's.
import type { Rect, TextLine } from '@/native/cm-vision';
import { readsAsRole } from './creators';
import type { LocalTuning } from './defaults';
import { bottom, overlapShare, right } from './geometry';
import { normalize, wordsOf } from './lines';

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
 * Whether the line says nothing but a sticker: every word is a number or one of a sticker's words. "WIDESCREEN", "Stock No.
 * 038", "10th ANNIVERSARY EDITION" and a shelf code of two numbers ("813.54") are stickers; "1984", "SE7EN" and "GREMLINS2"
 * are titles. One word on its own is a sticker only when no item is called that, or when it is a run of digits too long to
 * be a year or a count. A single letter says nothing: it neither makes a sticker nor saves a line from being one, so
 * "COLLECTOR'S" is a sticker while "Vol 2", whose 2 is a number and does count, is one too. A line of nothing but single
 * letters ("M*A*S*H", "E.T.") has no word at all and reads as a sticker here; `titleCandidates` gives it back when it is the
 * biggest lettering on the piece.
 */
export function readsAsSticker(text: string, tuning: LocalTuning): boolean {
  const words = wordsOf(text).filter((w) => w.length > 1 || isNumber(w));
  if (words.length === 0) return true;
  if (words.length === 1) return STICKER_ALONE.has(words[0]) || (isNumber(words[0]) && words[0].length >= tuning.stickerCodeDigits);
  return words.every((w) => isNumber(w) || STICKER_WORDS.has(w) || STICKER_ALONE.has(w));
}

/**
 * Whether a sticker is only a code: numbers, or initials, with no sticker word in it, and at least two characters, so a stray
 * mark ("+T-") is never one. It is what a title can look like ("M*A*S*H", "E.T.", "10000"); "WIDESCREEN" and "Stock No. 038" never are.
 */
const isCode = (text: string) => {
  const words = wordsOf(text);
  return words.join('').length >= 2 && !words.some((w) => STICKER_WORDS.has(w) || STICKER_ALONE.has(w));
};

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

/** What Vision read of one piece, and which of its lines are readings of one another. */
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
   * The clearer of two readings of one place: the one the other readings of that place agree with, else the surer and
   * longer, which is all the choice went by before (#617). Their boxes say nothing here: see the head of this file.
   */
  clearest(a: TextLine, b: TextLine, pool: TextLine[], place: TextLine[] = []): TextLine {
    const [mine, theirs] = [this.agreement(a, pool), this.agreement(b, pool)];
    if (theirs === mine) {
      const clean = this.inside(a, b, place) ?? this.inside(b, a, place);
      if (clean) return clean;
    }
    return theirs > mine || (theirs === mine && weight(b) > weight(a)) ? b : a;
  }

  /**
   * `small` when it is the clean reading inside `big`, a fat reading of the same place that caught more: down a spine, its words
   * are some of `big`'s and more than `cleanReadingShare` of them, and no other line of the place reads a word only `big` read.
   * The junk in `big` then does not set the place's lettering.
   */
  private inside(small: TextLine, big: TextLine, place: TextLine[]): TextLine | undefined {
    const [s, b] = [this.said(small), this.said(big)];
    if (!isSideways(small) || !isSideways(big)) return undefined;
    if (s.size === 0 || s.size >= b.size || ![...s].every((w) => b.has(w))) return undefined;
    if (s.size <= b.size * this.tuning.cleanReadingShare) return undefined;
    const extra = [...b].filter((w) => !s.has(w));
    const readElsewhere = place.filter((k) => k !== big && k !== small).some((k) => extra.some((w) => this.said(k).has(w)));
    return readElsewhere ? undefined : small;
  }

  /**
   * The lines grouped into places: a line joins a place when its box and a box already in it overlap by `onePlaceOverlap`
   * of the smaller one. A box inside another box is the same place, so a damaged reading of a spine, a reading that caught
   * two rows at once, and the clean reading of the title all land together, while the next spine along stands apart.
   */
  places(pool: TextLine[]): TextLine[][] {
    const places: TextLine[][] = [];
    for (const l of pool) {
      const joined = places.filter((p) => p.some((k) => overlapShare(k.box, l.box) >= this.tuning.onePlaceOverlap));
      const home = joined[0] ?? [];
      if (joined.length === 0) places.push(home);
      home.push(l);
      // One line can bridge two places that had not met: they are one place from here on.
      for (const other of joined.slice(1)) {
        home.push(...other);
        places.splice(places.indexOf(other), 1);
      }
    }
    return places;
  }

  /**
   * The second row of a title printed over two rows of one spine: the same lettering, running beside the chosen row for
   * most of it and set tight against it, in another place, saying other words. Another reading of the chosen row is none
   * of those. A title that ends on a joining word ("Witchcraft for") is not finished, so the row that continues it needs to run
   * beside it for less.
   */
  secondRow(chosen: TextLine, pool: TextLine[]): TextLine | undefined {
    const sideways = isSideways(chosen);
    const unfinished = this.tuning.titleRowJoinWords.includes(wordsOf(chosen.text).pop() ?? '');
    const along = unfinished ? this.tuning.titleRowJoinOverlap : this.tuning.titleRowOverlap;
    const size = letters(chosen);
    const beside = (k: TextLine) => {
      if (isSideways(k) !== sideways || this.samePlace(chosen, k) || this.sameText(chosen, k)) return false;
      if (!sameSize(letters(k), size, this.tuning.titleRowLetters)) return false;
      // The two rows of one title overlap across the line by a fair part of a letter: a block of print is set tight, and
      // its boxes run into each other. Two items' print at best grazes, so an award line, a publisher and the next spine
      // along are all left where they are, and only a real second row is read as part of the title.
      const over = sideways
        ? Math.min(right(chosen.box), right(k.box)) - Math.max(chosen.box.x, k.box.x)
        : Math.min(bottom(chosen.box), bottom(k.box)) - Math.max(chosen.box.y, k.box.y);
      return alongShare(chosen.box, k.box, sideways) >= along && over >= size * this.tuning.titleRowTouch;
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

/**
 * The lines that can be the piece's title: a sticker and a line that names the author's standing are never an item's title,
 * whatever size they are printed, with one exception (#631). A sticker that is only a code, numbers or initials with no
 * sticker word in it, is a title when it is printed in bigger lettering than every line that stays: "M*A*S*H" or "E.T."
 * printed big on a spine, where a stock number or shelf code is printed small. A sticker word never gets back in, however big.
 * The piece's own lines are the candidates when nothing else is left.
 */
function titleCandidates(lines: TextLine[], tuning: LocalTuning): TextLine[] {
  const stays = lines.filter((l) => !readsAsSticker(l.text, tuning) && !readsAsRole(l.text));
  const biggest = Math.max(0, ...stays.map(letters));
  const bigCodes = lines.filter((l) => readsAsSticker(l.text, tuning) && isCode(l.text) && !readsAsRole(l.text) && letters(l) > biggest);
  const candidates = lines.filter((l) => stays.includes(l) || bigCodes.includes(l));
  return candidates.length > 0 ? candidates : lines;
}

/** A trailing "BOOK 2" on a stack is the series number, not part of the title. */
const isBookNumber = (text: string) => /^book\s*\d+$/i.test(text.trim());

/** The words of two letters or more: a name's initial says nothing about whose name it is. */
const longWords = (text: string) => new Set(wordsOf(text).filter((w) => w.length >= 2));

/**
 * Whether this title is a name printed on another piece of the shot: every one of its words of two letters or more is on one line
 * of another piece. A series' author is printed on every spine of the series; a title is printed on one.
 */
export function namedElsewhere(title: string, others: TextLine[]): boolean {
  const named = longWords(title);
  return (
    named.size > 0 &&
    others.some((o) => {
      const said = longWords(o.text);
      return [...named].every((w) => said.has(w));
    })
  );
}

/**
 * The flat words stacked one a row down a spine, read top to bottom: the longest chain of two or more lines that are not
 * sideways, each set under the last within half the wider line's width of its center, at most `stackRowTuck` letter heights up into
 * it and at most `stackRowGap` below its foot. A trailing "BOOK n" is dropped, and at least two lines must remain.
 */
function stackOf(lines: TextLine[], tuning: LocalTuning): TextLine[] | undefined {
  const flat = lines.filter((l) => !isSideways(l) && /\p{L}/u.test(l.text)).sort((a, b) => a.box.y - b.box.y);
  const chains: TextLine[][] = [];
  for (const l of flat) {
    const home = chains.find((chain) => {
      const last = chain[chain.length - 1];
      const across = Math.abs(l.box.x + l.box.width / 2 - (last.box.x + last.box.width / 2));
      const gap = l.box.y - bottom(last.box);
      return across <= Math.max(last.box.width, l.box.width) / 2 && gap >= -tuning.stackRowTuck * last.box.height && gap <= tuning.stackRowGap * last.box.height;
    });
    if (home) home.push(l);
    else chains.push([l]);
  }
  const best = chains.filter((c) => c.length >= 2).sort((a, b) => b.length - a.length)[0];
  if (!best) return undefined;
  const kept = [...best];
  while (kept.length > 0 && isBookNumber(kept[kept.length - 1].text)) kept.pop();
  return kept.length >= 2 ? kept : undefined;
}

/**
 * Whether the stack repeats the title: each row of today's title has a row of the stack with the same text, the stack has more rows
 * than the title and at least `stackTitleMinRows`, and every row of it is one word, as a series name heading a title stacked a
 * word a row is ("CRESCENT / CITY / HOUSE / of / SKY"). Such a stack is the whole title, series words and all.
 */
function repeatsTitle(stack: TextLine[], rows: TextLine[], tuning: LocalTuning): boolean {
  return (
    stack.length >= tuning.stackTitleMinRows &&
    stack.length > rows.length &&
    stack.every((s) => wordsOf(s.text).length === 1) &&
    rows.every((r) => stack.some((s) => normalize(s.text) === normalize(r.text)))
  );
}

/**
 * The stack that is the title in place of `today`'s, when all three hold: it shares no word with today's title; today's title is a name
 * printed on another piece (the series author, sideways and big); and the stack is no publisher's imprint nor a stacked author name
 * (every word of it printed on other pieces too). A stack that repeats today's title (`repeatsTitle`) is the title whole, so it may
 * share words with it and stands even where a neighboring spine reads today's title as its own; every other check stays.
 */
function stackTitle(lines: TextLine[], rows: TextLine[], sibling: boolean, others: TextLine[], tuning: LocalTuning): TextLine[] | undefined {
  const stack = stackOf(lines, tuning);
  if (!stack) return undefined;
  const holds = repeatsTitle(stack, rows, tuning);
  if (sibling && !holds) return undefined;
  const today = rows.map((l) => l.text.trim()).join(' ');
  const text = stack.map((l) => l.text).join(' ');
  const mine = new Set(wordsOf(today));
  if (!holds && wordsOf(text).some((w) => mine.has(w))) return undefined;
  if (!namedElsewhere(today, others)) return undefined;
  if (wordsOf(text).some((w) => tuning.stackPublisherWords.includes(w) || tuning.stackStudioWords.includes(w))) return undefined;
  if (readsAsSticker(text, tuning) || readsAsRole(text)) return undefined;
  const elsewhere = new Set(others.flatMap((o) => [...longWords(o.text)]));
  const own = [...longWords(text)];
  if (own.length > 0 && own.every((w) => elsewhere.has(w))) return undefined;
  return stack;
}

/**
 * The piece's title, or nothing when the piece holds no line at all. `others` are the lines of the shot's other pieces: with them, flat
 * words stacked on this spine can be the title where the biggest lettering is the series author's name printed on the others too; without
 * them, nothing is stacked. A line a tile read added (it has a `region`) is never the title on its own and never the author: it only
 * counts in a stack. `siblings` are the titles the other pieces read: a title a neighboring spine reads as its own is a series title, and no
 * stack replaces it.
 */
export function shelfTitleOf(lines: TextLine[], tuning: LocalTuning, others: TextLine[] = [], siblings: string[] = []): ShelfTitle | undefined {
  if (lines.length === 0) return undefined;
  const readings = new Readings(lines, tuning);
  const own = lines.filter((l) => l.region === undefined);
  const said = titleCandidates(own.length > 0 ? own : lines, tuning);
  // One reading per place, then the place printed in the biggest lettering.
  const clearest = readings.places(said).map((place) => place.reduce((a, b) => readings.clearest(a, b, lines, place)));
  const chosen = clearest.reduce((a, b) => (letters(b) > letters(a) || (letters(b) === letters(a) && weight(b) > weight(a)) ? b : a));
  const second = readings.secondRow(chosen, said);
  const rows = second ? inRowOrder(chosen, second) : [chosen];
  const text = rows.map((l) => l.text.trim()).join(' ');
  const same = (t: string) => normalize(t) === normalize(text);
  const stack = stackTitle(lines, rows, siblings.some(same), others, tuning);
  if (stack) {
    // Today's title line goes to `rest`, so the name it printed is the creator.
    const rest = lines.filter((l) => !stack.includes(l) && l.region === undefined && !readsAsSticker(l.text, tuning));
    return { line: stack[0], text: stack.map((l) => l.text.trim()).join(' '), rest };
  }
  // The author is looked for among what the title did not use, and never among the stickers: a shelf piece's creator is a
  // guess, but a guess of "WIDESCREEN", or of the title's own second row, costs the server a right title (creators.ts).
  const rest = lines.filter((l) => !rows.includes(l) && !readsAsSticker(l.text, tuning) && (l.region === undefined || own.length === 0));
  return { line: chosen, text, rest };
}
