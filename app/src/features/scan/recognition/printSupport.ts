// Whether the shelf's own print backs up a title only the model read. On a shelf shot the model's reads carry no box of their own, so a
// read that is none of Vision's items goes out as an item beside them, and the model invents titles: most of the titles it alone adds are
// wrong, and most of those share no word with anything Vision's text read on that shelf. A title the print does not have is not added.
// Pure: words in, a yes or no out. The server mirrors these rules (`PrintSupport.cs`, used by `EvalCombination`), so a change here is a
// change there. The numbers are in defaults.ts (`PrintSupportTuning`).
import { DEFAULT_PRINT_SUPPORT_TUNING, type PrintSupportTuning } from './defaults';

/** Words that join a title together and say nothing about which one it is: a title is not backed by "the" on the shelf. */
const STOP_WORDS = new Set(['the', 'a', 'an', 'of', 'and', 'to', 'in', 'on', 'for', 'at', 'by', 'with', 'from', 'or', 'is']);

/** The lowercased words of a text: what is left when it is split on anything that is not a letter or a digit. */
const splitWords = (text: string): string[] => text.toLowerCase().split(/[^\p{L}\p{N}]+/u).filter((w) => w.length > 0);

/** The words of a model-read title that can back it up: stop words and one-letter words say nothing about which title it is. */
export function titleWords(title: string, tuning: PrintSupportTuning = DEFAULT_PRINT_SUPPORT_TUNING): string[] {
  return splitWords(title).filter((w) => w.length >= tuning.minWordLength && !STOP_WORDS.has(w));
}

/** Every word of every line Vision read on the shot: all its lines, not only the ones that became a piece's title. */
export function printedWords(lines: readonly { text: string }[]): Set<string> {
  const printed = new Set<string>();
  for (const line of lines) for (const word of splitWords(line.text)) printed.add(word);
  return printed;
}

/** Whether `a` becomes `b` by changing, adding or dropping one letter. */
function oneEditApart(a: string, b: string): boolean {
  if (Math.abs(a.length - b.length) > 1) return false;
  const [short, long] = a.length <= b.length ? [a, b] : [b, a];
  let at = 0;
  while (at < short.length && short[at] === long[at]) at++;
  return short.length === long.length ? short.slice(at + 1) === long.slice(at + 1) : short.slice(at) === long.slice(at + 1);
}

/**
 * Whether `word` is on the shelf: a print word is it, is it with one trailing "s" more or less, or (when both are long enough that one wrong
 * letter is a misread and not another word) is one edit away from it.
 */
function isPrinted(word: string, printed: ReadonlySet<string>, tuning: PrintSupportTuning): boolean {
  if (printed.has(word) || printed.has(`${word}s`) || (word.endsWith('s') && printed.has(word.slice(0, -1)))) return true;
  if (word.length < tuning.fuzzyMinLength) return false;
  for (const candidate of printed) if (candidate.length >= tuning.fuzzyMinLength && oneEditApart(word, candidate)) return true;
  return false;
}

/**
 * Whether the shelf's print backs up `title`, as the model read it (not a release's title): it has at least one word, and at least two thirds of
 * them are printed (`isPrinted`). "The First for a Red October" has three words, so "RED" and "OCTOBER" back it; "The Lion, the Witch and the
 * Wardrobe" has three, and "KIMBA THE LION" backs one of them.
 */
export function printSupports(title: string, printed: ReadonlySet<string>, tuning: PrintSupportTuning = DEFAULT_PRINT_SUPPORT_TUNING): boolean {
  const words = titleWords(title, tuning);
  if (words.length === 0) return false;
  const found = words.filter((w) => isPrinted(w, printed, tuning)).length;
  return found / words.length >= tuning.minPrintedShare;
}
