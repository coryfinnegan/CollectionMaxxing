// The Apple on-device model reads the picture; Vision text then gives each item a box. The model is a hint:
// what comes out is read fields and a confidence, and the server decides what it matches.
import type { ModelItem, TextLine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { type EngineDeps, defaultId } from './VisionPipelineEngine';
import { promptOf } from './defaults';
import { readLines, normalize } from './lines';
import { mapCompleteness, mapKind } from './mapping';
import { matchBoxes, type BoxMatch } from './matching';
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

  constructor(private readonly deps: EngineDeps) {
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
        onItem(this.toReadItem(m, match, idFor(m.order)));
      });
    };

    const modelStarted = this.now();
    // A cover shot depicts one item, so the model is asked for one (#504); a shelf shot gets the full snap.
    const maxItems = isSingleItemShot(options.shotKind) ? 1 : settings.engine.maxItemsPerSnap;
    const result = await this.deps.vision
      .readItems(still.uri, { prompt: promptOf(settings), timeoutMs: settings.engine.modelTimeoutMs, maxItems }, onModelItem)
      .catch(() => undefined);
    const modelMs = this.now() - modelStarted;
    await emitting;
    if (!result || result.outcome === 'unavailable') throw new ModelUnusableError('unavailable');
    const failure = FAILURES[result.outcome];
    if (failure) throw new ModelUnusableError(failure);

    // The reading itself is matched all at once, so lines go to the item they fit best.
    const lines = await linesPromise;
    const matches = matchBoxes(
      result.items.map((m) => m.visibleText),
      lines,
      settings.local,
    );
    const items = result.items.map((m, i) => this.toReadItem(m, matches[i], idFor(m.order)));
    const total = this.now() - started;
    return {
      items,
      unreadableCount: result.unreadableCount,
      engine: this.name,
      timings: { totalMs: total, modelMs, visionMs: total - modelMs },
    };
  }

  private toReadItem(m: ModelItem, match: BoxMatch, localId: string): ReadItem {
    const { kind, format } = mapKind(m.kind);
    const item: ReadItem = {
      localId,
      text: textOf(m.visibleText, match.lines),
      title: m.title,
      kind,
      order: m.order,
      confidence: m.confidence,
    };
    if (m.creator) item.creator = m.creator;
    if (format) item.format = format;
    if (m.platform) item.platform = m.platform;
    if (m.editionMarks) item.edition = m.editionMarks;
    const completeness = mapCompleteness(m.completeness);
    if (completeness) item.completeness = completeness;
    if (match.box) item.box = match.box;
    return item;
  }
}

/** The model's hint, plus any matched Vision line the hint does not already contain. */
function textOf(hint: string, lines: TextLine[]): string {
  const known = normalize(hint);
  const extra = lines.map((l) => l.text).filter((t) => !known.includes(normalize(t)));
  return [hint, ...extra].join(' ').trim();
}
