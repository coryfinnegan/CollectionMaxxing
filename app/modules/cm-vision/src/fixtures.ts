import barcodeScenes from '../fixtures/barcodeScenes.json';
import detectBarcodes from '../fixtures/detectBarcodes.json';
import detectRectangles from '../fixtures/detectRectangles.json';
import featurePrint from '../fixtures/featurePrint.json';
import featurePrintDistance from '../fixtures/featurePrintDistance.json';
import readItems from '../fixtures/readItems.json';
import recognizeText from '../fixtures/recognizeText.json';
import type { DetectedCode, FeaturePrint, FixtureRecord, ReadItemsResult, Rect, TextLine } from './types';

export interface FixtureSet {
  /** `orientation` absent means 0. */
  recognizeText: FixtureRecord<{ imageUri: string; orientation?: 90 | 270 }, TextLine[]>[];
  readItems: FixtureRecord<{ imageUri: string }, ReadItemsResult>[];
  detectRectangles: FixtureRecord<{ imageUri: string }, Rect[]>[];
  detectBarcodes: FixtureRecord<{ imageUri: string }, DetectedCode[]>[];
  featurePrint: FixtureRecord<{ imageUri: string; cropBox?: Rect }, FeaturePrint>[];
  featurePrintDistance: FixtureRecord<{ a: FeaturePrint; b: FeaturePrint }, number>[];
  /** Each output item is one camera callback's worth of codes. */
  barcodeScenes: FixtureRecord<{ scene: string }, DetectedCode[][]>[];
}

/** The hand-written fixtures; a dev build will later record real ones in the same format. */
export const fixtures = {
  recognizeText,
  readItems,
  detectRectangles,
  detectBarcodes,
  featurePrint,
  featurePrintDistance,
  barcodeScenes,
} as FixtureSet;

/** Order-insensitive deep equality for JSON values (undefined properties count as absent). */
export function sameJson(a: unknown, b: unknown): boolean {
  return canonical(a) === canonical(b);
}

function canonical(value: unknown): string {
  return JSON.stringify(value, (_key, v) =>
    v && typeof v === 'object' && !Array.isArray(v)
      ? Object.fromEntries(Object.entries(v as Record<string, unknown>).sort(([x], [y]) => x.localeCompare(y)))
      : v,
  );
}
