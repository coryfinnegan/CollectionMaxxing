// What the printed text of a single cover says about the kind of item it is. The weaker engines (the OCR-text tier, the Vision tier) often get the
// kind wrong: a Criterion Blu-ray read as a book, a film read as "other". A few lines are only ever printed on one kind of item:
//   - "The Criterion Collection" is only ever on a film;
//   - a console's name (PlayStation, Nintendo, ...) is on a game;
//   - a bestseller line ("A novel", "National bestseller") is on a book.
// Pure: Vision's text lines in, a kind or nothing out. The numbers (word limit, how many wrong characters OCR may have made) are
// `CoverFormatTuning` in defaults.ts, where a test requires every tunable value to live.
import type { TextLine } from '@/native/cm-vision';
import { fold } from './coverFormat';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING } from './defaults';
import { wordsOf } from './lines';

/** The kinds a cover's text can name; the words `ReadItem.kind` uses. */
export type CueKind = 'film' | 'game' | 'book';

/** Whether the two strings are at most `limit` single-character edits apart. */
function withinEdits(a: string, b: string, limit: number): boolean {
  if (Math.abs(a.length - b.length) > limit) return false;
  let previous = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const row = [i];
    for (let j = 1; j <= b.length; j++) {
      row[j] = Math.min(previous[j] + 1, row[j - 1] + 1, previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    }
    previous = row;
  }
  return previous[b.length] <= limit;
}

// ---- Film: The Criterion Collection ----------------------------------------------------------------------------------

/** Trademark signs are letters to the text reader's normalizer ("BESTSELLER™" becomes "bestsellertm"), and never part of what the line says. */
const plain = (text: string): string => text.replace(/[™℠]/gu, '');

/** The words as `fold` leaves them, so "CRITERI0N" and "BIu-ray"-style trades of letters and digits still read. */
const foldedWords = (text: string): string[] => fold(text).split(' ').filter(Boolean);

const CRITERION = fold('criterion');
const COLLECTION = fold('collection');
/** The tail of "collection" when the logo is set sideways and split in two. */
const COLLECTION_TAIL = fold('lection');

/**
 * The Criterion Collection's name anywhere on the cover. The word "criterion" alone could be a title, so it needs "the" before it, the
 * start of "collection" after it, or the word "collection" (or its tail) on a line of its own.
 */
function mentionsCriterion(texts: string[]): boolean {
  const spoken = texts.map(foldedWords);
  const isCriterion = (word: string) => withinEdits(word, CRITERION, 1);
  const named = spoken.some((words) =>
    words.some((word, i) => isCriterion(word) && (words[i - 1] === 'the' || words[i + 1]?.startsWith('co'))),
  );
  if (named) return true;
  const hasCriterion = spoken.some((words) => words.some(isCriterion));
  const hasCollection = spoken.some((words) => words.some((word) => word === COLLECTION_TAIL || withinEdits(word, COLLECTION, 1)));
  return hasCriterion && hasCollection;
}

// ---- Game: a console's name ------------------------------------------------------------------------------------------

/** Names long enough to be found inside a run of OCR noise ("PlayStationJ|{"), or split in two ("Game Boy"). Looked for in the squashed line. */
const LONG_CONSOLES = ['playstation', 'nintendo', 'gameboy'];
/** Names found only as a word of their own, with or without a number after them ("Xbox 360", "XBOX360"). */
const SHORT_CONSOLES = new Set(['xbox', 'sega', 'snes', 'n64', 'wii', 'psp', 'ps2', 'ps3', 'ps4', 'ps5', 'gamecube', 'dreamcast']);

/**
 * A short line that names a console. "Switch" is deliberately not on the list: it is a common title word, and a Nintendo Switch case
 * says "Nintendo" as well. A long line that happens to mention a console is a blurb, not the console's name.
 */
function namesConsole(text: string, tuning: CoverFormatTuning): boolean {
  const words = wordsOf(text);
  if (words.length === 0 || words.length > tuning.consoleLineMaxWords) return false;
  const squashed = words.join('');
  return LONG_CONSOLES.some((name) => squashed.includes(name)) || words.some((word) => SHORT_CONSOLES.has(word) || SHORT_CONSOLES.has(word.replace(/\d+$/, '')));
}

// ---- Book: a bestseller line -----------------------------------------------------------------------------------------

/**
 * Lines a book prints about itself, as words. A line counts only when it is one of these and nothing more: "From the best-selling novel by
 * the author of ..." is a film cover's line about the book it was made from, and "novel by" or "based on the novel" are the same.
 */
const BOOK_LINES = [
  'a novel',
  'a memoir',
  'bestseller',
  'national bestseller',
  'international bestseller',
  'new york times bestseller',
  '1 new york times bestseller',
  'number one new york times bestseller',
  'new york times bestselling author',
  '1 new york times bestselling author',
].map((phrase) => fold(phrase).replace(/ /g, ''));

/** The whole line says it, give or take the wrong characters OCR makes (a long phrase is allowed more of them than a short one). */
function isBookLine(text: string, tuning: CoverFormatTuning): boolean {
  const squashed = fold(text).replace(/ /g, '');
  return BOOK_LINES.some((phrase) => withinEdits(squashed, phrase, Math.floor(phrase.length / tuning.bookLineCharsPerEdit)));
}

// ---- Together --------------------------------------------------------------------------------------------------------

/**
 * Every kind these lines of print name, which is usually none: the Criterion Collection a film, a console's name a game, a bestseller
 * line a book. A set, because print that names two kinds names neither (see `coverKind`). Shared with the shelf's own print
 * (shelfKind.ts), which adds the words a film shelf prints and reads the answer the same way.
 */
export function printedKinds(lines: TextLine[], tuning: CoverFormatTuning = DEFAULT_COVER_FORMAT_TUNING): Set<CueKind> {
  const texts = lines.map((l) => plain(l.text));
  const found = new Set<CueKind>();
  if (mentionsCriterion(texts)) found.add('film');
  if (texts.some((text) => namesConsole(text, tuning))) found.add('game');
  if (texts.some((text) => isBookLine(text, tuning))) found.add('book');
  return found;
}

/** The one kind a set of print cues names, or nothing when it names none or more than one. */
export const theOneKind = (found: Set<CueKind>): CueKind | undefined => (found.size === 1 ? [...found][0] : undefined);

/**
 * The one kind the cover's text names, or nothing: a cover whose lines name two different kinds (a Criterion film that says "A novel", a
 * console beside a bestseller line) says nothing, and the read stands as it was.
 */
export function coverKind(lines: TextLine[], tuning: CoverFormatTuning = DEFAULT_COVER_FORMAT_TUNING): CueKind | undefined {
  return theOneKind(printedKinds(lines, tuning));
}
