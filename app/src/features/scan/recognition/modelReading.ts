// Turning one model item into a read item. Shared by the picture reader (AppleModelEngine) and the OCR text
// extractor (FmTextEngine, spike #505): same mapping, same box matching, whichever input the model read.
import type { ModelItem, TextLine } from '@/native/cm-vision';
import { normalize } from './lines';
import { mapCompleteness, mapKind } from './mapping';
import type { BoxMatch } from './matching';
import type { ReadItem } from './types';

export function toReadItem(m: ModelItem, match: BoxMatch, localId: string): ReadItem {
  const { kind, format } = mapKind(m.kind);
  const item: ReadItem = {
    localId,
    text: textOf(m.visibleText, match.lines),
    title: m.title,
    kind,
    order: m.order,
    confidence: m.confidence,
  };
  if (m.creator) item.creator = m.creator;
  if (format) item.format = format;
  if (m.platform) item.platform = m.platform;
  if (m.editionMarks) item.edition = m.editionMarks;
  const completeness = mapCompleteness(m.completeness);
  if (completeness) item.completeness = completeness;
  if (match.box) item.box = match.box;
  return item;
}

/** The model's hint, plus any matched Vision line the hint does not already contain. */
export function textOf(hint: string, lines: TextLine[]): string {
  const known = normalize(hint);
  const extra = lines.map((l) => l.text).filter((t) => !known.includes(normalize(t)));
  return [hint, ...extra].join(' ').trim();
}
