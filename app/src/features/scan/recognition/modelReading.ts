// Turning one model item into a read item. Shared by the picture reader (AppleModelEngine) and the OCR text
// extractor (FmTextEngine, spike #505): same mapping, same box matching, whichever input the model read.
import type { ModelItem, TextLine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { DEFAULT_SINGLE_SHOT_TUNING, type RecognitionSettings, type SingleShotTuning } from './defaults';
import { normalize } from './lines';
import { mapCompleteness, mapKind } from './mapping';
import type { BoxMatch } from './matching';
import type { ReadItem, ReadOptions } from './types';
import type { EngineDeps } from './VisionPipelineEngine';

/** What the two model engines (the picture reader and the OCR-text extractor) are built from. */
export interface ModelEngineDeps extends EngineDeps {
  /** How long a single cover may take; `DEFAULT_SINGLE_SHOT_TUNING` unless given. */
  singleShotTuning?: SingleShotTuning;
}

/**
 * How long the model may take on this read. A single-item shot has a shorter budget of its own (it reads in seconds, and a model that hangs
 * on one holds up the scanner), capped by the server's: the server can only make it shorter. A shelf shot keeps the server's budget.
 */
export function modelTimeoutMs(settings: RecognitionSettings, options: ReadOptions, tuning: SingleShotTuning = DEFAULT_SINGLE_SHOT_TUNING): number {
  const budget = settings.engine.modelTimeoutMs;
  return isSingleItemShot(options.shotKind) ? Math.min(budget, tuning.modelTimeoutMs) : budget;
}

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
