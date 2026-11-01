// What the printed text of a whole shelf says about the kind of shelf it is. A spine is a few words, so the engines read no kind
// for one at all: the Vision tier sends `other` and the picture model guesses from the picture (it called every tape on one shelf
// a book). The shelf's own print says it plainly. A few words are only ever printed on one kind of shelf:
//   - a disc or tape format (DVD, VHS, Blu-ray), the aspect the picture is in (Widescreen) or a running time ("92 Minutes") is a film;
//   - a console's name (PlayStation, Nintendo, ...) is a game;
//   - a bestseller line ("A novel", "National bestseller") is a book.
// These appear on two to six lines of each film shelf of the golden set and on none of its book shelves. The kinds a single cover's
// print names are the same (coverKind.ts), so the two read one rule: print that names two kinds names neither, and the snap keeps
// no kind, as it did before. Pure: Vision's text lines in, a kind or nothing out.
//
// The answer is the snap's kind, never a read's: the server searches every provider for it as before, and only lists releases of
// that kind first where they score level with the others (KindContext). A book on a tape shelf is still found as a book.
import type { TextLine } from '@/native/cm-vision';
import { FILM_FORMAT_PHRASES, linesPrint } from './coverFormat';
import { type CueKind, printedKinds, theOneKind } from './coverKind';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING } from './defaults';
import { normalize } from './lines';

/** What a case prints about the shape of its picture. Not a title word, and never on a book. */
const ASPECT_PHRASES: readonly string[] = ['widescreen', 'wide screen', 'fullscreen', 'full screen', 'letterbox', 'letterboxed', 'anamorphic'];

/**
 * A running time as a case prints it: "92 Minutes", "90 Minutes/Color", "47 min". The word is what makes it one, so a year ("1999")
 * and a page count ("352 pages") are not, and the word must be whole, so a title that ends in those letters ("SAMIN") is not either.
 * Read from the normalized line, not the folded one: folding trades digits for letters, which is what a logo needs and a number is not.
 */
const RUNNING_TIME = /(?:^|[^0-9a-z])\d+ ?(?:min|mins|minute|minutes)(?:[^a-z]|$)/;

const namesARunningTime = (lines: TextLine[]): boolean => lines.some((l) => RUNNING_TIME.test(normalize(l.text)));

/** Whether the shelf prints something only a film prints: the format of the disc or tape, the aspect of the picture, a running time. */
function namesAFilm(lines: TextLine[], tuning: CoverFormatTuning): boolean {
  return linesPrint(lines, FILM_FORMAT_PHRASES, tuning) || linesPrint(lines, ASPECT_PHRASES, tuning) || namesARunningTime(lines);
}

/**
 * The one kind this shelf's print names, or nothing when it names none or more than one. The kind belongs to the whole snap: it is
 * what the shelf is a shelf of, not what any one spine is.
 */
export function shelfKind(lines: TextLine[], tuning: CoverFormatTuning = DEFAULT_COVER_FORMAT_TUNING): CueKind | undefined {
  const found = printedKinds(lines, tuning);
  if (namesAFilm(lines, tuning)) found.add('film');
  return theOneKind(found);
}
