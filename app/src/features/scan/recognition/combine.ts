// Putting the picture model's items beside the Vision pipeline's on a shelf shot. The two find different spines, and often read the
// same one differently, so the shelf reading is Vision's items plus every model item that is not one of them read again. A model item
// that reads the same thing as the Vision item at its place is that item read again: the Vision item keeps its title, box and id, and
// takes what it lacks (the model's kind and format steer the server's search). A model item that reads something else is a second read
// of that place, and goes out beside it: the server shows whichever of the two grounds better and skips the other (`ReadPlaces`), because
// the phone cannot know which read finds the item. A Vision piece holding several model reads is several spines merged into one piece, so
// it is dropped and each model read inside it stands. Pure: items in, items out.
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
 * Whether `other` read the same thing `candidate` read: its title says at least `tuning.duplicateTitleShare` of what the candidate's
 * says. Vision's piece of a spine holds the author's line too, so "Dune" against "DUNE FRANK HERBERT" is the same read; "Dune Messiah"
 * against "DUNE" is not.
 */
export function readsTheSame(candidate: ReadItem, other: ReadItem, tuning: LocalTuning): boolean {
  return titleCoverage(candidate.title, other.title, tuning) >= tuning.duplicateTitleShare;
}

/**
 * Whether two reads sit in one place: their boxes overlap by at least `tuning.sameItemOverlap` of the smaller one. Both boxes are
 * normalized to the same still, whichever engine made them. A read with no box sits nowhere, so it is in no place at all.
 */
export function samePlace(first: ReadItem, second: ReadItem, tuning: LocalTuning): boolean {
  return !!first.box && !!second.box && overlapShare(first.box, second.box) >= tuning.sameItemOverlap;
}

/**
 * Whether `candidate` is the item `other` read again: the same place (`samePlace`) and the same reading of it (`readsTheSame`). The
 * same place read differently is not the same item read again but a second read of the place, which the server settles; five films
 * all titled "HIGHLANDER" are five items, because their boxes are apart.
 *
 * Only when either item has no box is the title all there is to go on: `other`'s title says at least `tuning.duplicateTitleShare` of
 * what the candidate's title says.
 */
export function isSameItem(candidate: ReadItem, other: ReadItem, tuning: LocalTuning): boolean {
  if (candidate.box && other.box) return samePlace(candidate, other, tuning) && readsTheSame(candidate, other, tuning);
  return readsTheSame(candidate, other, tuning);
}

/** True when none of the `existing` items is the candidate read again. */
export function isNewItem(candidate: ReadItem, existing: ReadItem[], tuning: LocalTuning): boolean {
  return sameItemAt(candidate, existing, tuning) < 0;
}

/**
 * Where `candidate` sits among `items`: the index of the item its box overlaps the most, at least `tuning.sameItemOverlap`. -1 when it
 * sits on none, or when it has no box (an item with no box sits nowhere). Where it sits only: what either read there says nothing.
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
 * The index of the item in `items` that `candidate` is a second read of (`isSameItem`), the one its box overlaps most where more than
 * one would do. -1 when the candidate is nobody's second read: a place nothing else saw, or a place another engine read as something
 * else.
 */
export function sameItemAt(candidate: ReadItem, items: ReadItem[], tuning: LocalTuning): number {
  let best = -1;
  let share = -1;
  items.forEach((item, i) => {
    if (!isSameItem(candidate, item, tuning)) return;
    const overlap = candidate.box && item.box ? overlapShare(candidate.box, item.box) : 0;
    if (overlap > share) {
      best = i;
      share = overlap;
    }
  });
  return best;
}

/**
 * The indexes of `base` items that hold more than one of `candidates` in places apart from each other: a Vision piece made of a run of
 * spines, which the model split. Such a piece is no place of its own, so the shelf reading drops it and keeps the model's reads of the
 * spines inside it. A spine the model read twice is one place, not two, so it does not make a piece a merged one.
 */
export function mergedPieces(base: ReadItem[], candidates: ReadItem[], tuning: LocalTuning): Set<number> {
  const inside = new Map<number, ReadItem[]>();
  candidates.forEach((candidate) => {
    const at = placeOf(candidate, base, tuning);
    if (at >= 0) inside.set(at, [...(inside.get(at) ?? []), candidate]);
  });
  const merged = new Set<number>();
  inside.forEach((items, at) => {
    if (items.some((a) => items.some((b) => !samePlace(a, b, tuning)))) merged.add(at);
  });
  return merged;
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
 * Puts what `candidate` knows into the item of `items` it is a second read of (`sameItemAt`), replacing that entry in place. Returns the
 * new item, or undefined when the candidate is nobody's second read or has nothing to add. Only a read of the same place lends its
 * fields: a read of a place that says something else is not a second read of it, and a read with no box sits nowhere, so its title alone
 * is not enough to lend a kind or a format.
 */
export function fillPlace(items: ReadItem[], candidate: ReadItem, tuning: LocalTuning): ReadItem | undefined {
  const at = sameItemAt(candidate, items, tuning);
  if (at < 0 || !samePlace(candidate, items[at], tuning)) return undefined;
  const filled = filledFrom(items[at], candidate);
  if (filled === items[at]) return undefined;
  items[at] = filled;
  return filled;
}

/** `base` with every candidate that is a second read of one of its items filling that item in (`filledFrom`). The items keep their order and ids. */
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
 * `item` as an item only the model saw: where it sits, the other engine found nothing or read something else. The server matches it like
 * any read but keeps it for review, never adding it by itself (`ReadItem.unconfirmed`).
 */
export function asUnconfirmed(item: ReadItem): ReadItem {
  return { ...item, unconfirmed: true };
}

/**
 * The candidates that are not an item already read (`isNewItem`), numbered after the existing items and otherwise left as their
 * engine made them (their own confidence, kind and box). Each is unconfirmed: nothing else read it that way.
 */
export function addableItems(candidates: ReadItem[], existing: ReadItem[], tuning: LocalTuning): ReadItem[] {
  const next = nextOrder(existing);
  return candidates.filter((c) => isNewItem(c, existing, tuning)).map((item, i) => asUnconfirmed({ ...item, order: next + i }));
}

/**
 * What a shelf shot read: the Vision pipeline's items, less the pieces that turned out to hold several model reads, each taking what it
 * lacks from the model's read of it, and then every model read that is not one of them read again. A model read of a place Vision read as
 * something else goes out beside it, so the server can show whichever of the two grounds better.
 */
export function shelfReading(base: ReadItem[], candidates: ReadItem[], tuning: LocalTuning): ReadItem[] {
  const merged = mergedPieces(base, candidates, tuning);
  const filled = enrichedItems(
    base.filter((_, i) => !merged.has(i)),
    candidates,
    tuning,
  );
  return [...filled, ...addableItems(candidates, filled, tuning)];
}
