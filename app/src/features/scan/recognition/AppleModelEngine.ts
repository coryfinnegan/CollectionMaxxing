// The Apple on-device model reads the picture; Vision text then gives each item a box. The model is a hint:
// what comes out is read fields and a confidence, and the server decides what it matches.
import type { ModelItem } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { defaultId } from './VisionPipelineEngine';
import { promptOf } from './defaults';
import { readLines } from './lines';
import { matchBoxes } from './matching';
import { type ModelEngineDeps, modelTimeoutMs, toReadItem } from './modelReading';
import { type FellBackFrom, ModelUnusableError, type ReadItem, type ReadOptions, type RecognitionEngine, type SnapReading, type Still } from './types';

const FAILURES: Record<string, FellBackFrom> = {
  declined: 'declined',
  timeout: 'timeout',
  contextExceeded: 'contextExceeded',
};

export class AppleModelEngine implements RecognitionEngine {
  readonly name = 'apple_model' as const;
  private readonly now: () => number;
  private readonly newId: () => string;
  /** The native stream carries no call id, so only one model read runs at a time. */
  private tail: Promise<unknown> = Promise.resolve();

  constructor(private readonly deps: ModelEngineDeps) {
    this.now = deps.now ?? Date.now;
    this.newId = deps.newId ?? defaultId;
  }

  async isAvailable(): Promise<boolean> {
    const caps = await this.deps.vision.modelCapabilities();
    return caps.available && caps.vision;
  }

  read(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions = {}): Promise<SnapReading> {
    const run = this.tail.then(() => this.readOne(still, onItem, options));
    this.tail = run.catch(() => undefined);
    return run;
  }

  private async readOne(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions): Promise<SnapReading> {
    const started = this.now();
    const settings = this.deps.settings();
    const linesPromise = readLines(this.deps.vision, still.uri, settings.local);
    const ids = new Map<number, string>();
    const idFor = (order: number) => {
      if (!ids.has(order)) ids.set(order, this.newId());
      return ids.get(order) as string;
    };

    // Items are emitted as they stream in, once Vision text is ready; each takes lines no earlier item took.
    const claimed = new Set<number>();
    let emitting: Promise<unknown> = Promise.resolve();
    const onModelItem = (m: ModelItem) => {
      emitting = emitting.then(async () => {
        const lines = await linesPromise;
        const [match] = matchBoxes([m.visibleText], lines, settings.local, claimed);
        onItem(toReadItem(m, match, idFor(m.order)));
      });
    };

    const modelStarted = this.now();
    // A cover shot depicts one item, so the model is asked for one (#504), and given a shorter time to answer; a shelf shot gets the full snap.
    const maxItems = isSingleItemShot(options.shotKind) ? 1 : settings.engine.maxItemsPerSnap;
    const result = await this.deps.vision
      .readItems(still.uri, { prompt: promptOf(settings), timeoutMs: modelTimeoutMs(settings, options, this.deps.singleShotTuning), maxItems }, onModelItem)
      .catch(() => undefined);
    const modelMs = this.now() - modelStarted;
    await emitting;
    if (!result || result.outcome === 'unavailable') throw new ModelUnusableError('unavailable');
    // A read that ran out of time keeps what it had read: a dense shelf streams for longer than the budget, and the items
    // that finished are real reads (the first eval run threw a whole shelf away). With nothing read, it falls back.
    const failure = FAILURES[result.outcome];
    if (failure && !(result.outcome === 'timeout' && result.items.length > 0)) throw new ModelUnusableError(failure);

    // The reading itself is matched all at once, so lines go to the item they fit best.
    const lines = await linesPromise;
    const matches = matchBoxes(
      result.items.map((m) => m.visibleText),
      lines,
      settings.local,
    );
    const items = result.items.map((m, i) => toReadItem(m, matches[i], idFor(m.order)));
    const total = this.now() - started;
    return {
      items,
      unreadableCount: result.unreadableCount,
      engine: this.name,
      timings: { totalMs: total, modelMs, visionMs: total - modelMs },
    };
  }

}
