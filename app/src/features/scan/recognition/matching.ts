// Giving each model item the box of the Vision text it read. Fuzzy on purpose (the model completes cut-off words),
// and never a guess: no match means no box.
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning } from './defaults';
import { union } from './geometry';
import { normalize, wordsOf } from './lines';

export interface BoxMatch {
  box?: Rect;
  /** The Vision lines that belong to the item. */
  lines: TextLine[];
}

interface Pair {
  line: number;
  item: number;
  score: number;
  specificity: number;
}

function wordsMatch(a: string, b: string, tuning: LocalTuning): boolean {
  if (a === b) return true;
  const shorter = a.length < b.length ? a : b;
  const longer = shorter === a ? b : a;
  return shorter.length >= tuning.prefixMinLength && longer.startsWith(shorter);
}

/**
 * `visibleTexts[i]` is the model's hint for item i, in order. A Vision line goes to the one item it fits best, and an
 * item never takes two lines with the same text (two identical spines are two lines for two items).
 * `claimed` holds line indexes earlier calls already gave away; it is updated in place.
 */
export function matchBoxes(visibleTexts: string[], lines: TextLine[], tuning: LocalTuning, claimed = new Set<number>()): BoxMatch[] {
  const itemWords = visibleTexts.map(wordsOf);
  const pairs: Pair[] = [];
  lines.forEach((l, line) => {
    if (claimed.has(line)) return;
    const words = wordsOf(l.text);
    if (words.length === 0) return;
    itemWords.forEach((iw, item) => {
      if (iw.length === 0) return;
      const hits = words.filter((w) => iw.some((x) => wordsMatch(w, x, tuning))).length;
      const score = hits / words.length;
      if (score >= tuning.minLineScore) pairs.push({ line, item, score, specificity: hits / iw.length });
    });
  });
  pairs.sort(
    (a, b) =>
      b.score - a.score || b.specificity - a.specificity || a.item - b.item || lines[a.line].box.x - lines[b.line].box.x,
  );

  const taken = new Set<number>();
  const assigned: number[][] = visibleTexts.map(() => []);
  for (const p of pairs) {
    if (taken.has(p.line)) continue;
    const text = normalize(lines[p.line].text);
    if (assigned[p.item].some((i) => normalize(lines[i].text) === text)) continue;
    taken.add(p.line);
    assigned[p.item].push(p.line);
  }

  return assigned.map((indexes, item) => {
    const words = itemWords[item];
    const covered = words.filter((w) => indexes.some((i) => wordsOf(lines[i].text).some((x) => wordsMatch(x, w, tuning)))).length;
    if (indexes.length === 0 || words.length === 0 || covered / words.length < tuning.minItemCoverage) return { lines: [] };
    indexes.forEach((i) => claimed.add(i));
    const matched = indexes.map((i) => lines[i]).sort((a, b) => a.box.y - b.box.y || a.box.x - b.box.x);
    return { box: union(matched.map((l) => l.box)), lines: matched };
  });
}
