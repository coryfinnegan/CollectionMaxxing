// Putting the Vision pipeline's items beside the picture model's on a shelf shot. The two find different spines, so the
// shelf reading is the model's items plus every Vision item the model did not already read. Pure: items in, items out.
import type { LocalTuning } from './defaults';
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
 * The Vision items that are not a model item read again: a Vision item is the same book when the model item's title says
 * at least `tuning.duplicateTitleShare` of what its own title says. They are numbered after the model's items and
 * otherwise left as the Vision engine made them (low confidence, their own boxes).
 */
export function addableItems(vision: ReadItem[], model: ReadItem[], tuning: LocalTuning): ReadItem[] {
  const fresh = vision.filter((v) => !model.some((m) => titleCoverage(v.title, m.title, tuning) >= tuning.duplicateTitleShare));
  const next = model.length === 0 ? 0 : Math.max(...model.map((m) => m.order)) + 1;
  return fresh.map((item, i) => ({ ...item, order: next + i }));
}
