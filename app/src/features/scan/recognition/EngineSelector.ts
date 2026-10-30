// Picks the engine for each snap. The Apple model when it can see pictures; the OCR-text extraction tier when the
// model can only read text, or when the picture read declines, runs out of time or room; Vision text for that one snap
// when neither model tier answers. The next snap tries the model again. A shelf shot the picture model read also gets
// the Vision pipeline's items the model did not find: the two engines find different spines.
import type { ModelCapabilities, ModelUnavailableReason, Rect, VisionEngine } from '@/native/cm-vision';
import { discardPicture, trackPicture } from '../pictureFiles';
import { isSingleItemShot } from '../shotkind/shotKind';
import { AppleModelEngine } from './AppleModelEngine';
import { FmTextEngine } from './FmTextEngine';
import { VisionPipelineEngine, type EngineDeps } from './VisionPipelineEngine';
import { addableItems } from './combine';
import { chooseCut, halfRegions, mergeHalves } from './halves';
import { readLines } from './lines';
import { type EngineName, type FellBackFrom, ModelUnusableError, type ReadItem, type ReadOptions, type SnapReading, type Still, type StillCropper } from './types';

export type AppleIntelligenceState = 'on' | ModelUnavailableReason | 'unknown';

export interface RecognitionState {
  /** The engine the next snap will try first. */
  engine: EngineName;
  appleIntelligence: AppleIntelligenceState;
}

export interface SelectorDeps extends EngineDeps {
  /** Needed to read a wide shot in halves; without it a wide shot is read whole. */
  cropper?: StillCropper;
}

const seesPictures = (caps: ModelCapabilities) => caps.available && caps.vision;
const extractsText = (caps: ModelCapabilities) => caps.available && caps.guidedGeneration;

export class EngineSelector {
  private readonly apple: AppleModelEngine;
  private readonly text: FmTextEngine;
  private readonly pipeline: VisionPipelineEngine;
  private readonly now: () => number;

  constructor(private readonly deps: SelectorDeps) {
    this.apple = new AppleModelEngine(deps);
    this.text = new FmTextEngine(deps);
    this.pipeline = new VisionPipelineEngine(deps);
    this.now = deps.now ?? Date.now;
  }

  private get vision(): VisionEngine {
    return this.deps.vision;
  }

  private async capabilities(): Promise<ModelCapabilities | undefined> {
    try {
      return await this.vision.modelCapabilities();
    } catch {
      return undefined;
    }
  }

  /** For Settings → Recognition. */
  async state(): Promise<RecognitionState> {
    const caps = await this.capabilities();
    if (caps && seesPictures(caps)) return { engine: 'apple_model', appleIntelligence: 'on' };
    if (caps && extractsText(caps)) return { engine: 'fm_text', appleIntelligence: 'on' };
    return { engine: 'vision_pipeline', appleIntelligence: caps ? (caps.reason ?? 'notSupported') : 'unknown' };
  }

  async read(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions = {}): Promise<SnapReading> {
    const started = this.now();
    const caps = await this.capabilities();
    if (!caps || !extractsText(caps)) return this.fallBack(still, onItem, 'unavailable', options);
    if (!seesPictures(caps)) return this.readText(still, onItem, options);
    try {
      const reading = await this.apple.read(still, onItem, options);
      // The Vision read starts once the model's items have streamed, so it never competes with them, and runs while a wide shot is re-read in halves.
      const vision = this.readVision(still, options);
      const whole = await this.maybeHalves(still, reading, started, options);
      return await this.withVisionItems(whole, vision, onItem, started);
    } catch (error) {
      return this.readText(still, onItem, options, error instanceof ModelUnusableError ? error.reason : 'unavailable');
    }
  }

  /**
   * The middle tier. When the picture read failed first, its reason is the snap's fellBackFrom: that says why the
   * preferred engine wasn't used, and a failed extraction on top changes nothing about it.
   */
  private async readText(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions, fellBackFrom?: FellBackFrom): Promise<SnapReading> {
    try {
      const reading = await this.text.read(still, onItem, options);
      return fellBackFrom === undefined ? reading : { ...reading, fellBackFrom };
    } catch (error) {
      const reason = fellBackFrom ?? (error instanceof ModelUnusableError ? error.reason : 'unavailable');
      return this.fallBack(still, onItem, reason, options);
    }
  }

  private async fallBack(still: Still, onItem: (item: ReadItem) => void, reason: FellBackFrom, options: ReadOptions): Promise<SnapReading> {
    const reading = await this.pipeline.read(still, onItem, options);
    return { ...reading, fellBackFrom: reason };
  }

  /** The Vision pipeline's own read of a shelf shot; nothing for a cover shot or when asked for the model alone. Never rejects. */
  private readVision(still: Still, options: ReadOptions): Promise<SnapReading | undefined> | undefined {
    if (options.combine === false || isSingleItemShot(options.shotKind)) return undefined;
    return this.pipeline.read(still, () => {}, options).catch(() => undefined);
  }

  /**
   * The model's reading plus the Vision items it did not have, streamed as they are added. They keep the engine's own fields
   * (low confidence, their own boxes); the snap stays the model's. When Vision failed or added nothing the model's reading
   * comes back as it was: this step never fails a snap.
   */
  private async withVisionItems(model: SnapReading, vision: Promise<SnapReading | undefined> | undefined, onItem: (item: ReadItem) => void, started: number): Promise<SnapReading> {
    const read = await vision;
    if (!read) return model;
    try {
      const added = addableItems(read.items, model.items, this.deps.settings().local);
      if (added.length === 0) return model;
      added.forEach(onItem);
      const total = this.now() - started;
      return { ...model, items: [...model.items, ...added], timings: { ...model.timings, totalMs: total, visionMs: total - (model.timings.modelMs ?? 0) } };
    } catch {
      return model;
    }
  }

  /** A shelf with more items than the config allows in one look is read as two halves cut where there is no text. */
  private async maybeHalves(still: Still, whole: SnapReading, started: number, options: ReadOptions): Promise<SnapReading> {
    const settings = this.deps.settings();
    const { cropper } = this.deps;
    if (!cropper || whole.items.length <= settings.splitting.halvesWhenItemsOver) return whole;
    const halves: string[] = []; // the two cut pictures are only read, then deleted
    const cut = async (region: Rect): Promise<Still> => {
      const half = await cropper.crop(still, region);
      halves.push(trackPicture(half.uri));
      return half;
    };
    try {
      const lines = await readLines(this.vision, still.uri, settings.local);
      const [leftRegion, rightRegion] = halfRegions(chooseCut(lines, settings.local), settings.local);
      const noop = () => {};
      const left = await this.apple.read(await cut(leftRegion), noop, options);
      const right = await this.apple.read(await cut(rightRegion), noop, options);
      const items = mergeHalves(
        { items: left.items, region: leftRegion },
        { items: right.items, region: rightRegion },
        settings.local,
      );
      const total = this.now() - started;
      return {
        items,
        unreadableCount: left.unreadableCount + right.unreadableCount,
        engine: 'apple_model',
        timings: {
          totalMs: total,
          modelMs: (whole.timings.modelMs ?? 0) + (left.timings.modelMs ?? 0) + (right.timings.modelMs ?? 0),
          visionMs: total - (whole.timings.modelMs ?? 0) - (left.timings.modelMs ?? 0) - (right.timings.modelMs ?? 0),
        },
      };
    } catch {
      return whole; // the whole-shot reading is still a reading
    } finally {
      await Promise.all(halves.map((uri) => discardPicture(uri)));
    }
  }
}
