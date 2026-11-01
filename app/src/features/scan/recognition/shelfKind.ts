// What the printed text of a whole shelf says about the kind of shelf it is. A spine is a few words, so the engines read no kind
// for one at all: the Vision tier sends `other` and the picture model guesses from the picture (it called every tape on one shelf
// a book). The shelf's own print says it plainly. A few words are only ever printed on one kind of shelf:
//   - a disc or tape format (DVD, VHS, Blu-ray), the aspect the picture is in (Widescreen) or a running time ("92 Minutes") is a film;
//   - a console's name (PlayStation, Nintendo, ...) is a game;
//   - a bestseller line ("A novel", "National bestseller") is a book.
// The kinds a single cover's print names are the same (coverKind.ts), so the two read one rule: print that names two kinds names
// neither, and the snap keeps no kind, as it did before. A book called "30-Minute Meals", or one DVD on a shelf of books, is not the
// shelf: a shelf's kind is printed on several of its spines. A spine is one piece the splitter makes, and it can be read as several
// lines (once in each orientation, or twice with a letter wrong), so lines are not spines. The print must name the kind on at least
// `SHELF_KIND_MIN_SPINES` of the pieces; a line that is in no piece can name a second kind, which vetoes, but is never a spine.
// Pure: Vision's text lines and the pieces made of them in, a kind or nothing out.
//
// The answer is the snap's kind, never a read's. The server uses it to order releases that score level; and when no read of the snap
// names that kind it also takes a kind the picture model guessed off the reads that deny it (the model called every tape on one shelf
// a book), which changes how those reads score (see KindContext on the server). It is why the print is held to two spines.
import type { TextLine } from '@/native/cm-vision';
import { FILM_FORMAT_PHRASES, linesPrint } from './coverFormat';
import { type CueKind, printedKinds, theOneKind } from './coverKind';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING } from './defaults';
import { normalize } from './lines';
import type { Piece } from './splitting';

/** What a case prints about the shape of its picture. Not a title word, and never on a book. */
const ASPECT_PHRASES: readonly string[] = ['widescreen', 'wide screen', 'fullscreen', 'full screen', 'letterbox', 'letterboxed', 'anamorphic'];

/** How many separate spines (pieces) must print a kind before the shelf is a shelf of it. */
const SHELF_KIND_MIN_SPINES = 2;

/**
 * A running time as a case prints it: "92 Minutes", "90 Minutes/Color", "98 mins", "47 min". The word is what makes it one, so a year
 * ("1999") and a page count ("352 pages") are not, and the word must be whole, so a title that ends in those letters ("SAMIN") is not
 * either. A case never prints the singular, so "4 Minute Mile" and "30-Minute Meals" (a hyphen reads as a space) are titles.
 * Read from the normalized line, not the folded one: folding trades digits for letters, which is what a logo needs and a number is not.
 */
const RUNNING_TIME = /(?:^|[^0-9a-z])\d+ ?(?:min|mins|minutes)(?:[^a-z]|$)/;

const namesARunningTime = (lines: TextLine[]): boolean => lines.some((l) => RUNNING_TIME.test(normalize(l.text)));

/** Whether the shelf prints something only a film prints: the format of the disc or tape, the aspect of the picture, a running time. */
function namesAFilm(lines: TextLine[], tuning: CoverFormatTuning): boolean {
  return linesPrint(lines, FILM_FORMAT_PHRASES, tuning) || linesPrint(lines, ASPECT_PHRASES, tuning) || namesARunningTime(lines);
}

/** Every kind these lines of print name: a cover's cues, and the words only a film shelf prints. */
function kindsNamed(lines: TextLine[], tuning: CoverFormatTuning): Set<CueKind> {
  const found = printedKinds(lines, tuning);
  if (namesAFilm(lines, tuning)) found.add('film');
  return found;
}

/**
 * The one kind this shelf's print names, when at least `SHELF_KIND_MIN_SPINES` of its pieces name it by their own lines. The whole print
 * is read for the veto: a line on any spine, or in no piece at all, that names a second kind (or the Criterion name split across two
 * lines) makes the answer nothing. A piece names the kind however many of its lines do, so a spine read twice is still one spine. The
 * kind belongs to the whole snap: it is what the shelf is a shelf of, not what any one spine is.
 */
export function shelfKind(lines: TextLine[], pieces: readonly Piece[], tuning: CoverFormatTuning = DEFAULT_COVER_FORMAT_TUNING): CueKind | undefined {
  const one = theOneKind(kindsNamed(lines, tuning));
  if (one === undefined) return undefined;
  const spines = pieces.filter((piece) => kindsNamed(piece.lines, tuning).has(one));
  return spines.length >= SHELF_KIND_MIN_SPINES ? one : undefined;
}
