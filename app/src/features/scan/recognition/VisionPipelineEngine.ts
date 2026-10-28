// The fallback engine: Vision text and rectangles, split in TypeScript, no model. Its titles are guesses, so every
// item says low confidence and leaves the finding to the server.
import type { VisionEngine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { creatorOf } from './creators';
import type { RecognitionSettings } from './defaults';
import { readLines } from './lines';
import { splitIntoPieces, wholeFrame, type Split } from './splitting';
import type { ReadItem, ReadOptions, RecognitionEngine, SnapReading, Still } from './types';

export interface EngineDeps {
  vision: VisionEngine;
  settings: () => RecognitionSettings;
  now?: () => number;
  newId?: () => string;
}

let sequence = 0;
export const defaultId = () => `read-${Date.now().toString(36)}-${(sequence += 1)}`;

export class VisionPipelineEngine implements RecognitionEngine {
  readonly name = 'vision_pipeline' as const;
  private readonly now: () => number;
  private readonly newId: () => string;

  constructor(private readonly deps: EngineDeps) {
    this.now = deps.now ?? Date.now;
    this.newId = deps.newId ?? defaultId;
  }

  isAvailable(): Promise<boolean> {
    return this.deps.vision.isAvailable();
  }

  async read(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions = {}): Promise<SnapReading> {
    const started = this.now();
    const settings = this.deps.settings();
    const lines = await readLines(this.deps.vision, still.uri, settings.local);
    // A cover shot is one item: the whole frame, never segmented (#504). Anything else may be a shelf row.
    if (isSingleItemShot(options.shotKind)) {
      return this.reading(wholeFrame(lines), settings, started, onItem);
    }
    const rectangles = await this.deps.vision.detectRectangles(still.uri).catch(() => []);
    return this.reading(splitIntoPieces(lines, rectangles, settings.splitting, settings.local), settings, started, onItem);
  }

  private reading(split: Split, settings: RecognitionSettings, started: number, onItem: (item: ReadItem) => void): SnapReading {
    const { pieces, unreadableCount } = split;
    const items = pieces.map((p, order): ReadItem => {
      const titleLine = p.lines.reduce((best, l) =>
        l.confidence * l.text.length > best.confidence * best.text.length ? l : best,
      );
      const creator = creatorOf(p.lines, titleLine, settings.local);
      return {
        localId: this.newId(),
        text: p.lines.map((l) => l.text).join(' '),
        title: titleLine.text,
        ...(creator ? { creator } : {}),
        kind: 'other',
        box: p.box,
        order,
        confidence: 'low',
      };
    });
    items.forEach(onItem);
    const total = this.now() - started;
    return { items, unreadableCount, engine: this.name, timings: { totalMs: total, visionMs: total } };
  }
}
