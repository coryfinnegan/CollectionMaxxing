import { SPINE_SCHEMA_MIN_PIECES } from './defaults';
import type { SnapReading } from './types';

/**
 * Which item schema the model reads a shelf with. Vision's own split says what the shot is: many spine pieces and no covers is a shelf of
 * spines, where the model writes only the fields that decide a match. Anything else (a cover framed as a segment, several covers, no Vision
 * reading at all) keeps the full item and its platform. The scanner and the eval runner both ask here, so the eval reads what ships.
 */
export function modelSchemaFor(vision: SnapReading | undefined): 'spine' | 'full' {
  const spines = (vision?.ocr?.pieces.length ?? 0) >= SPINE_SCHEMA_MIN_PIECES && !vision?.coverCount;
  return spines ? 'spine' : 'full';
}
