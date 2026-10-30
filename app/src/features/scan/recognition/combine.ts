// Putting the picture model's items beside the Vision pipeline's on a shelf shot. The two find different spines, so the shelf
// reading is Vision's items plus every model item that is not already one of them. The same place is the same item, whatever the
// two engines made of its title. A model item on a Vision item does not replace it: the Vision item keeps its title, box and id, and
// takes what it lacks (the model's kind and format steer the server's search). Pure: items in, items out.
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

/**
 * Where `candidate` sits among `items`: the index of the item its box overlaps the most, at least `tuning.sameItemOverlap`. -1 when it
 * sits on none, or when it has no box (an item with no box sits nowhere; only its title could say, and that is not enough to lend fields).
 */
export function placeOf(candidate: ReadItem, items: ReadItem[], tuning: LocalTuning): number {
  const { box } = candidate;
  if (!box) return -1;
  let best = -1;
  let share = 0;
  items.forEach((item, i) => {
    const overlap = item.box ? overlapShare(box, item.box) : 0;
    if (overlap >= tuning.sameItemOverlap && overlap > share) {
      best = i;
      share = overlap;
    }
  });
  return best;
}

/**
 * `item` with what it lacks taken from `other`, the same item read by another engine. `item` keeps its title, box, localId, order, text and
 * confidence, and everything it already has. It takes the kind only when its own is `other` (it was not told), and the fields that belong
 * to a kind (format, platform, completeness, a game's product code) only when the kinds then agree. A code the item already has is never
 * replaced. The same object comes back when there is nothing to take.
 */
export function filledFrom(item: ReadItem, other: ReadItem): ReadItem {
  const fill: Partial<ReadItem> = {};
  const takesKind = item.kind === 'other' && other.kind !== 'other';
  if (takesKind) fill.kind = other.kind;
  const agree = (takesKind ? other.kind : item.kind) === other.kind;
  if (agree && !item.format && other.format) fill.format = other.format;
  if (agree && !item.platform && other.platform) fill.platform = other.platform;
  if (agree && !item.completeness && other.completeness) fill.completeness = other.completeness;
  if (agree && !item.productCode && other.productCode) fill.productCode = other.productCode;
  if (!item.creator && other.creator) fill.creator = other.creator;
  if (!item.edition && other.edition) fill.edition = other.edition;
  return Object.keys(fill).length === 0 ? item : { ...item, ...fill };
}

/**
 * Puts what `candidate` knows into the item of `items` it sits on (`placeOf`), replacing that entry in place. Returns the new item, or
 * undefined when the candidate sits on none or has nothing to add.
 */
export function fillPlace(items: ReadItem[], candidate: ReadItem, tuning: LocalTuning): ReadItem | undefined {
  const at = placeOf(candidate, items, tuning);
  if (at < 0) return undefined;
  const filled = filledFrom(items[at], candidate);
  if (filled === items[at]) return undefined;
  items[at] = filled;
  return filled;
}

/** `base` with every candidate that sits on one of its items filling that item in (`filledFrom`). The items keep their order and ids. */
export function enrichedItems(base: ReadItem[], candidates: ReadItem[], tuning: LocalTuning): ReadItem[] {
  const out = [...base];
  candidates.forEach((candidate) => fillPlace(out, candidate, tuning));
  return out;
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
