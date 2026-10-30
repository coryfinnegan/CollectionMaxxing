// Picks the engine for each snap. The Apple model when it can see pictures; the OCR-text extraction tier when the
// model can only read text, or when the picture read declines, runs out of time or room; Vision text for that one snap
// when neither model tier answers. The next snap tries the model again. A shelf shot is read by the Vision pipeline first, and
// its items stream at once; the model then adds the items it found where Vision found nothing (combine.ts): Vision finds most of
// the spines in about a second, and the model finds different ones. A single cover read by any of the three tiers is checked against
// the case's shape and logos and the cover's printed text (CoverCueEngine.ts), which the engines often get wrong.
import type { ModelCapabilities, ModelUnavailableReason, Rect, VisionEngine } from '@/native/cm-vision';
import { discardPicture, trackPicture } from '../pictureFiles';
import { isSingleItemShot } from '../shotkind/shotKind';
import { AppleModelEngine } from './AppleModelEngine';
import { withCoverCue } from './CoverCueEngine';
import { FmTextEngine } from './FmTextEngine';
import { VisionPipelineEngine } from './VisionPipelineEngine';
import { addableItems, enrichedItems, fillPlace, isNewItem, nextOrder } from './combine';
import type { CoverFormatTuning } from './defaults';
import { chooseCut, halfRegions, mergeHalves } from './halves';
import { readLines } from './lines';
import type { ModelEngineDeps } from './modelReading';
import { type EngineName, type FellBackFrom, ModelUnusableError, type ReadItem, type ReadOptions, type RecognitionEngine, type SnapReading, type Still, type StillCropper } from './types';

export type AppleIntelligenceState = 'on' | ModelUnavailableReason | 'unknown';

export interface RecognitionState {
  /** The engine the next snap will try first. */
  engine: EngineName;
  appleIntelligence: AppleIntelligenceState;
}

export interface SelectorDeps extends ModelEngineDeps {
  /** Needed to read a wide shot in halves; without it a wide shot is read whole. */
  cropper?: StillCropper;
  /** What a cover's case says about its format, and how long to wait for it; `DEFAULT_COVER_FORMAT_TUNING` unless given. */
  coverFormatTuning?: CoverFormatTuning;
}

const seesPictures = (caps: ModelCapabilities) => caps.available && caps.vision;
const extractsText = (caps: ModelCapabilities) => caps.available && caps.guidedGeneration;

export class EngineSelector {
  /** The picture model with nothing around it: the halves of a wide shot are other pictures, so the cover's cue is not theirs. */
  private readonly model: AppleModelEngine;
  private readonly apple: RecognitionEngine;
  private readonly text: RecognitionEngine;
  private readonly pipeline: RecognitionEngine;
  private readonly now: () => number;

  /**
   * The two lower tiers as this selector reads with them, cover cue and all. The eval runner reads each tier on its own, and reads these, so
   * what it measures is what the scanner does.
   */
  readonly tiers: { fmText: RecognitionEngine; pipeline: RecognitionEngine };

  constructor(private readonly deps: SelectorDeps) {
    const cued = (engine: RecognitionEngine) => withCoverCue(engine, deps);
    this.model = new AppleModelEngine(deps);
    this.apple = cued(this.model);
    this.text = cued(new FmTextEngine(deps));
    this.pipeline = cued(new VisionPipelineEngine(deps));
    this.tiers = { fmText: this.text, pipeline: this.pipeline };
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
    if (options.combine !== false && !isSingleItemShot(options.shotKind)) return this.readShelf(still, onItem, options, started);
    // A single cover is corrected by what its case and its text say (the engines are wrapped); the model alone is read as the model read it.
    const read = await this.readModel(still, onItem, options, started);
    return typeof read === 'string' ? this.fallBack(still, onItem, read, options) : read;
  }

  /**
   * A shelf shot: Vision's items first, streamed at once (it reads a shelf in about a second and finds most of the spines, where the model
   * takes many times longer and invents titles); then the model reads, and adds only the items it found where Vision found nothing. A
   * model item on a Vision item changes nothing but what that item lacks: Vision keeps the title, box, localId and order, and takes the
   * model's kind and format (Vision's `other` searches books too), creator, platform, edition and completeness.
   *
   * The snap's engine and fellBackFrom say what the model did, not who found most of the items, so the model's health stays readable
   * in the stats: when a model tier answered, the snap is that tier's, with the reason the picture read failed if the text tier had to
   * answer; when none did, Vision's reading stands as the snap and the model's failure is the fellBackFrom, as it was when Vision was the
   * fallback. Vision failing loses nothing of the model's; only both failing fails the snap.
   */
  private async readShelf(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions, started: number): Promise<SnapReading> {
    const local = this.deps.settings().local;
    const emit = (item: ReadItem) => {
      try {
        onItem(item);
      } catch {
        // a failing store must not fail the snap, or lose the items after it
      }
    };

    let vision: SnapReading | undefined;
    let visionFailure: unknown;
    try {
      vision = await this.pipeline.read(still, emit, options);
    } catch (error) {
      visionFailure = error;
    }
    const found = vision?.items ?? [];

    // A model item streams if it is somewhere Vision found nothing, numbered after Vision's. One on a Vision item adds what that item lacks (its
    // kind, its format), and the Vision item streams again under its own localId, so the receipt row updates in place. The reading the model
    // ends with supersedes what streamed.
    const current = [...found];
    let next = nextOrder(found);
    const streamNew = (item: ReadItem) => {
      if (isNewItem(item, found, local)) {
        emit({ ...item, order: next++ });
        return;
      }
      const filled = fillPlace(current, item, local);
      if (filled) emit(filled);
    };
    const model = await this.readModel(still, streamNew, options, started);

    const total = this.now() - started;
    if (typeof model === 'string') {
      if (!vision) throw visionFailure instanceof Error ? visionFailure : new Error('Neither Vision nor the model could read this shelf.');
      return { ...vision, fellBackFrom: model, timings: { ...vision.timings, totalMs: total } };
    }
    const items = [...enrichedItems(found, model.items, local), ...addableItems(model.items, found, local)];
    return { ...model, items, timings: { ...model.timings, totalMs: total, visionMs: total - (model.timings.modelMs ?? 0) } };
  }

  /**
   * The model tiers in order: the picture read (re-read in halves when the shelf is wide), then OCR-text extraction when the picture
   * read gave nothing. When the picture read failed first, its reason is the snap's fellBackFrom: that says why the preferred engine
   * wasn't used, and a failed extraction on top changes nothing about it. Resolves with that reason when no tier answered.
   */
  private async readModel(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions, started: number): Promise<SnapReading | FellBackFrom> {
    let reason: FellBackFrom = 'unavailable';
    try {
      const reading = await this.apple.read(still, onItem, options);
      return await this.maybeHalves(still, reading, started, options);
    } catch (error) {
      if (error instanceof ModelUnusableError) reason = error.reason;
    }
    try {
      return { ...(await this.text.read(still, onItem, options)), fellBackFrom: reason };
    } catch {
      return reason;
    }
  }

  /** The middle tier on its own, for a phone whose model can only read text. */
  private async readText(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions): Promise<SnapReading> {
    try {
      return await this.text.read(still, onItem, options);
    } catch (error) {
      return this.fallBack(still, onItem, error instanceof ModelUnusableError ? error.reason : 'unavailable', options);
    }
  }

  private async fallBack(still: Still, onItem: (item: ReadItem) => void, reason: FellBackFrom, options: ReadOptions): Promise<SnapReading> {
    const reading = await this.pipeline.read(still, onItem, options);
    return { ...reading, fellBackFrom: reason };
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
      const left = await this.model.read(await cut(leftRegion), noop, options);
      const right = await this.model.read(await cut(rightRegion), noop, options);
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
