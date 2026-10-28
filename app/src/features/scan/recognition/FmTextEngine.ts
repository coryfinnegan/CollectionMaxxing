// Spike #505: the OCR-text tier. Vision reads the text (which works on every iPhone back to iOS 26),
// then the on-device model extracts typed items from that text — no picture reaches the model, so no vision
// capability is needed. Boxes still come from matching the model's visibleText against the Vision lines, exactly
// like the picture reader; only the model's input differs. Prototype: the prompt is the same server-owned prompt
// the picture read uses, and the EngineSelector does not try this engine yet — the lead routes that after the spike.
import type { ModelItem, TextLine } from '@/native/cm-vision';
import { type EngineDeps, defaultId } from './VisionPipelineEngine';
import { promptOf } from './defaults';
import { readLines } from './lines';
import { matchBoxes } from './matching';
import { toReadItem } from './modelReading';
import { type FellBackFrom, ModelUnusableError, type ReadItem, type RecognitionEngine, type SnapReading, type Still } from './types';

const FAILURES: Record<string, FellBackFrom> = {
  declined: 'declined',
  timeout: 'timeout',
  contextExceeded: 'contextExceeded',
};

/** The one OCR text shape the extractor sends: the cleaned Vision lines, one per line. */
export const ocrTextOf = (lines: TextLine[]): string => lines.map((l) => l.text).join('\n');

export class FmTextEngine implements RecognitionEngine {
  readonly name = 'fm_text' as const;
  private readonly now: () => number;
  private readonly newId: () => string;
  /** The native stream carries no call id, so only one extraction runs at a time. */
  private tail: Promise<unknown> = Promise.resolve();

  constructor(private readonly deps: EngineDeps) {
    this.now = deps.now ?? Date.now;
    this.newId = deps.newId ?? defaultId;
  }

  async isAvailable(): Promise<boolean> {
    const caps = await this.deps.vision.modelCapabilities();
    return caps.available && caps.guidedGeneration;
  }

  read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> {
    const run = this.tail.then(() => this.readOne(still, onItem));
    this.tail = run.catch(() => undefined);
    return run;
  }

  private async readOne(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> {
    const started = this.now();
    const settings = this.deps.settings();
    const lines = await readLines(this.deps.vision, still.uri, settings.local);
    const total = () => this.now() - started;
    // No text is an empty reading, not a model call: there is nothing to extract from.
    if (lines.length === 0) {
      return { items: [], unreadableCount: 0, engine: this.name, timings: { totalMs: total(), visionMs: total() } };
    }
    const ids = new Map<number, string>();
    const idFor = (order: number) => {
      if (!ids.has(order)) ids.set(order, this.newId());
      return ids.get(order) as string;
    };

    // Items are emitted as they stream in; each takes lines no earlier item took.
    const claimed = new Set<number>();
    let emitting: Promise<unknown> = Promise.resolve();
    const onModelItem = (m: ModelItem) => {
      emitting = emitting.then(async () => {
        const [match] = matchBoxes([m.visibleText], lines, settings.local, claimed);
        onItem(toReadItem(m, match, idFor(m.order)));
      });
    };

    const modelStarted = this.now();
    const result = await this.deps.vision
      .extractItems(
        ocrTextOf(lines),
        { prompt: promptOf(settings), timeoutMs: settings.engine.modelTimeoutMs, maxItems: settings.engine.maxItemsPerSnap },
        onModelItem,
      )
      .catch(() => undefined);
    const modelMs = this.now() - modelStarted;
    await emitting;
    if (!result || result.outcome === 'unavailable') throw new ModelUnusableError('unavailable');
    const failure = FAILURES[result.outcome];
    if (failure) throw new ModelUnusableError(failure);

    // The reading itself is matched all at once, so lines go to the item they fit best.
    const matches = matchBoxes(
      result.items.map((m) => m.visibleText),
      lines,
      settings.local,
    );
    const items = result.items.map((m, i) => toReadItem(m, matches[i], idFor(m.order)));
    const elapsed = total();
    return {
      items,
      unreadableCount: result.unreadableCount,
      engine: this.name,
      timings: { totalMs: elapsed, modelMs, visionMs: elapsed - modelMs },
    };
  }
}
