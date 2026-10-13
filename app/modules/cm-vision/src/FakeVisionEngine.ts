import { fixtures, sameJson, type FixtureSet } from './fixtures';
import type { DetectedCode, FeaturePrint, Rect, TextLine, VisionEngine } from './types';

/** Replays fixture JSON; fails loudly for an input it has no fixture for. */
export class FakeVisionEngine implements VisionEngine {
  constructor(private readonly set: FixtureSet = fixtures) {}

  async isAvailable(): Promise<boolean> {
    return true;
  }

  async recognizeText(imageUri: string): Promise<TextLine[]> {
    return this.replay('recognizeText', { imageUri });
  }

  async detectRectangles(imageUri: string): Promise<Rect[]> {
    return this.replay('detectRectangles', { imageUri });
  }

  async detectBarcodes(imageUri: string): Promise<DetectedCode[]> {
    return this.replay('detectBarcodes', { imageUri });
  }

  async featurePrint(imageUri: string, cropBox?: Rect): Promise<FeaturePrint> {
    return this.replay('featurePrint', cropBox ? { imageUri, cropBox } : { imageUri });
  }

  async featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number> {
    if (sameJson(a, b)) return 0;
    const hit =
      this.find('featurePrintDistance', { a, b }) ?? this.find('featurePrintDistance', { a: b, b: a });
    if (hit === undefined) throw notFound('featurePrintDistance', { a, b });
    return hit;
  }

  private find<K extends keyof FixtureSet>(name: K, input: unknown): FixtureSet[K][number]['output'] | undefined {
    const records = this.set[name] as { input: unknown; output: unknown }[];
    return records.find((r) => sameJson(r.input, input))?.output as FixtureSet[K][number]['output'] | undefined;
  }

  private replay<K extends keyof FixtureSet>(name: K, input: unknown): FixtureSet[K][number]['output'] {
    const output = this.find(name, input);
    if (output === undefined) throw notFound(name, input);
    return output;
  }
}

function notFound(name: string, input: unknown): Error {
  return new Error(`FakeVisionEngine: no ${name} fixture for input ${JSON.stringify(input)}`);
}
