import { SPINE_SCHEMA_MIN_PIECES } from './defaults';
import { modelSchemaFor } from './modelSchema';
import type { SnapReading } from './types';

const reading = (pieces: number, coverCount?: number): SnapReading =>
  ({ ocr: { lines: [], pieces: Array.from({ length: pieces }, () => [0]) }, ...(coverCount ? { coverCount } : {}) }) as unknown as SnapReading;

describe('the model schema for a shelf', () => {
  it('is the spine schema when Vision found enough pieces and no covers', () => {
    expect(modelSchemaFor(reading(SPINE_SCHEMA_MIN_PIECES))).toBe('spine');
  });
  it('is the full schema one piece short of that', () => {
    expect(modelSchemaFor(reading(SPINE_SCHEMA_MIN_PIECES - 1))).toBe('full');
  });
  it('is the full schema when Vision found a cover among the pieces', () => {
    expect(modelSchemaFor(reading(SPINE_SCHEMA_MIN_PIECES + 5, 1))).toBe('full');
  });
  it('is the full schema with no Vision reading', () => {
    expect(modelSchemaFor(undefined)).toBe('full');
  });
});
