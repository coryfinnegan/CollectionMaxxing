// The fixed shot-classification fixture set (design §3): each framing pins the kind it must classify as. Adding a
// fixture is adding behavior; changing an expectation is a deliberate contract change.
import type { ShotKind, ShotSignals } from './shotKind';

export interface ShotFixture {
  name: string;
  signals: ShotSignals;
  expected: ShotKind;
}

const base: ShotSignals = {
  barcodesInView: 0,
  rectangleCoverage: 0,
  rectangleCount: 0,
  dominantAspect: null,
  textRows: 0,
  tappedCover: false,
  mediaHint: undefined,
  framesWithoutBarcode: 0,
  gateFired: false,
};

const shelf: ShotSignals = { ...base, rectangleCoverage: 0.25, rectangleCount: 6, textRows: 6, gateFired: true };
const cover: ShotSignals = { ...base, rectangleCoverage: 0.6, rectangleCount: 1, dominantAspect: 0.68, textRows: 3 };

export const SHOT_FIXTURES: ShotFixture[] = [
  // A barcode in view always wins, whatever else the frame holds.
  { name: 'barcode beats a shelf', signals: { ...shelf, barcodesInView: 1 }, expected: 'barcode' },
  { name: 'barcode beats a single cover', signals: { ...cover, barcodesInView: 2 }, expected: 'barcode' },
  { name: 'barcode beats a tapped cover', signals: { ...cover, barcodesInView: 1, tappedCover: true }, expected: 'barcode' },
  // One dominant cover-like rectangle.
  { name: 'book cover', signals: cover, expected: 'single_cover' },
  { name: 'game case', signals: { ...cover, rectangleCoverage: 0.5, dominantAspect: 0.62 }, expected: 'single_cover' },
  { name: 'tapped cover with weak geometry', signals: { ...base, rectangleCoverage: 0.2, rectangleCount: 1, textRows: 1, tappedCover: true }, expected: 'single_cover' },
  // Shelves: rows, spines, or the gate firing.
  { name: 'shelf of six spines', signals: shelf, expected: 'shelf_segment' },
  { name: 'two spine rectangles, no text', signals: { ...base, rectangleCoverage: 0.25, rectangleCount: 2 }, expected: 'shelf_segment' },
  { name: 'two text rows, no rectangles', signals: { ...base, textRows: 2 }, expected: 'shelf_segment' },
  { name: 'gate fired on an ambiguous frame', signals: { ...base, rectangleCoverage: 0.2, rectangleCount: 1, textRows: 1, gateFired: true }, expected: 'shelf_segment' },
  // Loose no-barcode games and movies.
  { name: 'loose cart, games hint', signals: { ...cover, mediaHint: 'games', framesWithoutBarcode: 12 }, expected: 'loose_item' },
  { name: 'loose disc, movies hint', signals: { ...cover, mediaHint: 'movies', framesWithoutBarcode: 30 }, expected: 'loose_item' },
  { name: 'games hint but the barcode may still arrive', signals: { ...cover, mediaHint: 'games', framesWithoutBarcode: 3 }, expected: 'single_cover' },
  { name: 'books hint never goes loose', signals: { ...cover, mediaHint: 'books', framesWithoutBarcode: 12 }, expected: 'single_cover' },
  { name: 'no hint never goes loose', signals: { ...cover, framesWithoutBarcode: 50 }, expected: 'single_cover' },
  { name: 'tapped loose geometry stays loose', signals: { ...cover, mediaHint: 'games', framesWithoutBarcode: 12, tappedCover: true }, expected: 'loose_item' },
  // Nothing recognized.
  { name: 'empty frame', signals: base, expected: 'unknown' },
  { name: 'one row and one small rectangle', signals: { ...base, rectangleCoverage: 0.2, rectangleCount: 1, textRows: 1 }, expected: 'unknown' },
  { name: 'wide poster, not cover-shaped', signals: { ...base, rectangleCoverage: 0.7, rectangleCount: 1, dominantAspect: 2.5 }, expected: 'unknown' },
  { name: 'small cover, under the area bar', signals: { ...base, rectangleCoverage: 0.3, rectangleCount: 1, dominantAspect: 0.7, textRows: 1 }, expected: 'unknown' },
];
