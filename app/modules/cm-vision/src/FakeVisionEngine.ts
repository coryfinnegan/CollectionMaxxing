import { fixtures, sameJson, type FixtureSet } from './fixtures';
import { linesInPicture } from './regions';
import { textRequest } from './textOptions';
import type {
  DetectedCode,
  DetectRectanglesOptions,
  DeviceStatus,
  FeaturePrint,
  ModelCapabilities,
  ModelItem,
  ReadItemsOptions,
  ReadItemsResult,
  RecognizeTextOptions,
  Rect,
  TextLine,
  TextRequestDescription,
  VisionEngine,
} from './types';

export const CAPABLE_MODEL: ModelCapabilities = { available: true, vision: true, guidedGeneration: true, contextSize: 4096 };

export interface FakeVisionOptions {
  capabilities?: ModelCapabilities;
  deviceStatus?: DeviceStatus;
  /** What this fake's OS is, before any option: its revisions, languages and build. Left out, the first `describeTextRequest` fixture. */
  textRequest?: TextRequestDescription;
}

/** Replays fixture JSON; fails loudly for an input it has no fixture for. */
export class FakeVisionEngine implements VisionEngine {
  private capabilities: ModelCapabilities;
  private status: DeviceStatus;
  private textRequestBase: TextRequestDescription;

  constructor(
    private readonly set: FixtureSet = fixtures,
    options: FakeVisionOptions = {},
  ) {
    this.capabilities = options.capabilities ?? CAPABLE_MODEL;
    this.status = options.deviceStatus ?? { thermalState: 'nominal', osVersion: '26.0' };
    this.textRequestBase = options.textRequest ?? fixtures.describeTextRequest[0].output;
  }

  /** Per-test configuration: the OS's own text request (its default and supported revisions, languages and build). */
  setTextRequest(description: TextRequestDescription): void {
    this.textRequestBase = description;
  }

  /** The OS's own request with what the options name written over it; refuses a revision the OS does not support, as the native module does. */
  async describeTextRequest(options?: RecognizeTextOptions): Promise<TextRequestDescription> {
    const base = this.textRequestBase;
    if (options?.revision !== undefined && !base.supportedRevisions.includes(options.revision)) {
      throw new Error(`Text recognition has no revision ${options.revision}; it supports ${base.supportedRevisions.join(', ')}.`);
    }
    return {
      ...base,
      revision: options?.revision ?? base.revision,
      recognitionLanguages: options?.languages ?? base.recognitionLanguages,
      automaticallyDetectsLanguage: options?.automaticallyDetectsLanguage ?? base.automaticallyDetectsLanguage,
    };
  }

  /** Per-test configuration, e.g. Apple Intelligence off or the model not ready. */
  setCapabilities(capabilities: ModelCapabilities): void {
    this.capabilities = capabilities;
  }

  setDeviceStatus(status: DeviceStatus): void {
    this.status = status;
  }

  async modelCapabilities(): Promise<ModelCapabilities> {
    return this.capabilities;
  }

  async deviceStatus(): Promise<DeviceStatus> {
    return this.status;
  }

  /** Streams each fixture item to `onItem` in `order`, then resolves. Never throws for a recorded outcome. */
  async readItems(imageUri: string, _options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult> {
    const result = this.replay('readItems', { imageUri });
    return this.stream(result, onItem);
  }

  /** Same replay as `readItems`, keyed by the exact OCR text instead of a picture. */
  async extractItems(ocrText: string, _options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult> {
    return this.stream(this.replay('extractItems', { ocrText }), onItem);
  }

  private stream(result: ReadItemsResult, onItem?: (item: ModelItem) => void): ReadItemsResult {
    const items = [...result.items].sort((a, b) => a.order - b.order);
    for (const item of items) onItem?.(item);
    return { ...result, items };
  }

  async isAvailable(): Promise<boolean> {
    return true;
  }

  /**
   * One recording per request shape: the picture, and what differs from the scanner's own request (a turned pass, the fast level, language
   * correction, one region). Every line says its pass; with regions each is read as its own request, recorded in the region's own frame, and put on
   * the whole picture the way the native wrapper does.
   */
  async recognizeText(imageUri: string, options?: RecognizeTextOptions): Promise<TextLine[]> {
    const { orientations, level, languageCorrection, regions } = textRequest(options);
    // A request names settings the recordings do not tell apart: they are replayed the same however they are asked, but an unsupported revision is refused first.
    if (options?.revision !== undefined) await this.describeTextRequest(options);
    const shape = {
      ...(level === 'fast' ? { level } : {}),
      ...(languageCorrection ? { languageCorrection } : {}),
    };
    const lines: TextLine[] = [];
    for (const orientation of orientations) {
      const turned = orientation === 0 ? {} : { orientation };
      if (regions.length === 0) {
        lines.push(...this.replay('recognizeText', { imageUri, ...turned, ...shape }).map((line) => ({ ...line, pass: orientation })));
        continue;
      }
      regions.forEach((region, index) => {
        const recorded = this.replay('recognizeText', { imageUri, ...turned, ...shape, region });
        lines.push(...linesInPicture(recorded.map((line) => ({ ...line, pass: orientation, region: index })), regions));
      });
    }
    return lines;
  }

  /** The options shape a real search; a recording is of one picture, so it is replayed the same however it is asked. */
  async detectRectangles(imageUri: string, _options?: DetectRectanglesOptions): Promise<Rect[]> {
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
