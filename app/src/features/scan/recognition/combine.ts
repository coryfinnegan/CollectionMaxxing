// Putting the picture model's items beside the Vision pipeline's on a shelf shot. The two find different spines, so the shelf
// reading is Vision's items plus every model item that is not already one of them. The same place is the same item, whatever the
// two engines made of its title. Pure: items in, items out.
import type { LocalTuning } from './defaults';
import { overlapShare } from './geometry';
import { wordsOf } from './lines';
import { wordsMatch } from './matching';
import type { ReadItem } from './types';

/** Words that join a title together and say nothing about which book it is: two titles do not match because both say "the". */
const FILLER = new Set(['the', 'a', 'an', 'and', 'of', 'in', 'on', 'to', 'for']);

/**
 * The share of `title`'s words that `other` also has: 1 when `other` says everything `title` says, 0 when it says none of
 * it. Filler words do not count as evidence (unless that is all the title has), and a cut-off word counts as the word
 * it starts, as in box matching.
 */
export function titleCoverage(title: string, other: string, tuning: LocalTuning): number {
  const words = wordsOf(title);
  const significant = words.filter((w) => !FILLER.has(w));
  const counted = significant.length > 0 ? significant : words;
  if (counted.length === 0) return 0;
  const otherWords = wordsOf(other);
  return counted.filter((w) => otherWords.some((x) => wordsMatch(w, x, tuning))).length / counted.length;
}

/**
 * Whether `candidate` is the item `other` read again. Two items with boxes are the same when their boxes overlap by at least
 * `tuning.sameItemOverlap` of the smaller one: that is the same place on the shelf, and the same place is the same physical item
 * whatever either engine titled it. Both boxes are normalized to the same still, whichever engine made them. Five films all titled
 * "HIGHLANDER" are five items, because their boxes are apart.
 *
 * Only when either item has no box is the title all there is to go on: `other`'s title says at least `tuning.duplicateTitleShare` of
 * what the candidate's title says.
 */
export function isSameItem(candidate: ReadItem, other: ReadItem, tuning: LocalTuning): boolean {
  if (candidate.box && other.box) return overlapShare(candidate.box, other.box) >= tuning.sameItemOverlap;
  return titleCoverage(candidate.title, other.title, tuning) >= tuning.duplicateTitleShare;
}

/** True when none of the `existing` items is the candidate read again. */
export function isNewItem(candidate: ReadItem, existing: ReadItem[], tuning: LocalTuning): boolean {
  return !existing.some((e) => isSameItem(candidate, e, tuning));
}

/** The order number after the highest of these items': where the next item added goes. */
export function nextOrder(items: ReadItem[]): number {
  return items.length === 0 ? 0 : Math.max(...items.map((i) => i.order)) + 1;
}

/**
 * The candidates that are not an item already read (`isNewItem`), numbered after the existing items and otherwise left as their
 * engine made them (their own confidence, kind and box).
 */
export function addableItems(candidates: ReadItem[], existing: ReadItem[], tuning: LocalTuning): ReadItem[] {
  const next = nextOrder(existing);
  return candidates.filter((c) => isNewItem(c, existing, tuning)).map((item, i) => ({ ...item, order: next + i }));
}
