// Test doubles for the engines: a FakeVisionEngine fed from small fixture sets, and a cropper that makes up still names.
import { FakeVisionEngine } from '../../../../modules/cm-vision/src/FakeVisionEngine';
import type { ModelItem, ReadItemsOptions, ReadItemsResult, Rect, TextLine } from '../../../../modules/cm-vision/src/types';
import { DEFAULT_LOCAL_TUNING } from './defaults';
import { ocrTextOf } from './FmTextEngine';
import { cleanLines } from './lines';
import type { Still, StillCropper } from './types';

type FixtureSet = NonNullable<ConstructorParameters<typeof FakeVisionEngine>[0]>;

const record = <I, O>(input: I, output: O) => ({ capturedAt: '2026-01-01T00:00:00Z', engine: 'test', input, output });

export interface Scene {
  /** What the model returns for this picture. */
  read?: ReadItemsResult;
  /** What the model extracts from this picture's OCR text (spike #505). */
  extract?: ReadItemsResult;
  /** Lines Vision reads upright. */
  text?: TextLine[];
  /** Lines Vision reads with the picture turned 90 degrees clockwise. */
  text90?: TextLine[];
  text270?: TextLine[];
  /** Lines Vision reads with the picture turned upside down. */
  text180?: TextLine[];
  rectangles?: Rect[];
}

export const okRead = (items: ModelItem[], unreadableCount = 0): ReadItemsResult => ({
  items,
  unreadableCount,
  outcome: 'ok',
  elapsedMs: 5,
});

export const failedRead = (outcome: ReadItemsResult['outcome'], items: ModelItem[] = []): ReadItemsResult => ({
  items,
  unreadableCount: 0,
  outcome,
  elapsedMs: 5,
});

export const line = (text: string, x: number, y: number, width: number, height: number, confidence = 0.9): TextLine => ({
  text,
  confidence,
  box: { x, y, width, height },
});

export const modelItem = (title: string, visibleText: string, order: number, extra: Partial<ModelItem> = {}): ModelItem => ({
  visibleText,
  title,
  kind: 'book',
  order,
  confidence: 'high',
  ...extra,
});

/** A FakeVisionEngine that also remembers what `readItems` was asked, and can hold a read open to test ordering. */
export class RecordingVision extends FakeVisionEngine {
  readonly readCalls: { imageUri: string; options: ReadItemsOptions }[] = [];
  readonly extractCalls: { ocrText: string; options: ReadItemsOptions }[] = [];
  private active = 0;
  maxConcurrentReads = 0;

  constructor(scenes: Record<string, Scene>, capabilities?: ConstructorParameters<typeof FakeVisionEngine>[1]) {
    const entries = Object.entries(scenes);
    const set = {
      readItems: entries.flatMap(([uri, s]) => (s.read ? [record({ imageUri: uri }, s.read)] : [])),
      extractItems: entries.flatMap(([uri, s]) =>
        s.extract ? [record({ ocrText: ocrTextOf(cleanLines([...(s.text ?? []), ...(s.text90 ?? []), ...(s.text270 ?? [])], DEFAULT_LOCAL_TUNING)) }, s.extract)] : [],
      ),
      recognizeText: entries.flatMap(([uri, s]) => [
        record({ imageUri: uri }, s.text ?? []),
        record({ imageUri: uri, orientation: 90 as const }, s.text90 ?? []),
        record({ imageUri: uri, orientation: 270 as const }, s.text270 ?? []),
        record({ imageUri: uri, orientation: 180 as const }, s.text180 ?? []),
      ]),
      detectRectangles: entries.map(([uri, s]) => record({ imageUri: uri }, s.rectangles ?? [])),
      detectBarcodes: [],
      featurePrint: [],
      featurePrintDistance: [],
      barcodeScenes: [],
    } as unknown as FixtureSet;
    super(set, capabilities);
  }

  override async readItems(imageUri: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void) {
    this.readCalls.push({ imageUri, options });
    this.active += 1;
    this.maxConcurrentReads = Math.max(this.maxConcurrentReads, this.active);
    try {
      await Promise.resolve();
      return await super.readItems(imageUri, options, onItem);
    } finally {
      this.active -= 1;
    }
  }

  override async extractItems(ocrText: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void) {
    this.extractCalls.push({ ocrText, options });
    return super.extractItems(ocrText, options, onItem);
  }
}

/** Makes up a still name for each crop and remembers the regions asked for. */
export class FakeCropper implements StillCropper {
  readonly regions: Rect[] = [];
  async crop(still: Still, region: Rect): Promise<Still> {
    this.regions.push(region);
    return { uri: `${still.uri}#crop${this.regions.length}`, width: still.width * region.width, height: still.height * region.height };
  }
}

export const STILL: Still = { uri: 'fixture://still', width: 3000, height: 4000 };

let counter = 0;
export const testIds = () => `id-${++counter}`;
