// The author beside the title, from the Vision lines of one piece (#438 stage 1). A guess like the
// Vision title: short, name-like, and never the title itself. Wrong guesses are cheap — the server asks
// the catalogs with title + author first and falls back to the title alone when that finds nothing.
import type { TextLine } from '@/native/cm-vision';
import type { LocalTuning } from './defaults';
import { normalize } from './lines';

/** Role words name the slot, never the person ("NEW YORK TIMES BESTSELLING AUTHOR"). */
const ROLE_WORDS = /\b(authors?|bestsell\w*)\b/i;

/** What a printed name is made of. */
const NAME_CHARS = /^[\p{L}\p{M}\s.'’&,()\-–—:]+$/u;

/** Whether the line names the author's slot rather than a person or an item ("#1 NEW YORK TIMES BESTSELLING AUTHOR"). */
export function readsAsRole(text: string): boolean {
  return ROLE_WORDS.test(text);
}

function looksLikeName(text: string, tuning: LocalTuning): boolean {
  const words = text.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0 || words.length > tuning.creatorMaxWords) return false;
  if ((text.match(/\p{L}/gu) ?? []).length < tuning.creatorMinLetters) return false;
  if (/\d/.test(text)) return false;
  if (ROLE_WORDS.test(text)) return false;
  return NAME_CHARS.test(text);
}

/**
 * The piece's author line, if one reads as a name. `title` is the piece's own title line: it and any
 * line that only repeats it are never the author. Of several name-like lines the most confident wins.
 */
export function creatorOf(lines: TextLine[], title: TextLine, tuning: LocalTuning): string | undefined {
  const titled = normalize(title.text);
  let best: TextLine | undefined;
  for (const l of lines) {
    if (l === title) continue;
    const text = l.text.trim();
    if (text === '') continue;
    const known = normalize(text);
    if (known === '' || known === titled || titled.includes(known) || known.includes(titled)) continue;
    if (!looksLikeName(text, tuning)) continue;
    if (!best || l.confidence > best.confidence) best = l;
  }
  return best?.text.trim();
}
