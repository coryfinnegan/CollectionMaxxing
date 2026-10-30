// The fallback engine: Vision text and rectangles, split in TypeScript, no model. Its titles are guesses, so every
// item says low confidence and leaves the finding to the server.
import type { TextLine, VisionEngine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { coverTitleOf } from './coverTitle';
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
    // A cover shot is one item: the whole frame, never segmented (#504), and its title is its biggest text. Anything else may be a shelf row.
    if (isSingleItemShot(options.shotKind)) {
      return this.reading(wholeFrame(lines), lines, settings, started, onItem, still);
    }
    const rectangles = await this.deps.vision.detectRectangles(still.uri).catch(() => []);
    return this.reading(splitIntoPieces(lines, rectangles, settings.splitting, settings.local), lines, settings, started, onItem);
  }

  /** `cover` is the still of a single-cover shot: its pieces take the biggest text as the title. A shelf piece takes its longest, surest line. */
  private reading(split: Split, lines: TextLine[], settings: RecognitionSettings, started: number, onItem: (item: ReadItem) => void, cover?: Still): SnapReading {
    const { pieces, unreadableCount } = split;
    const items = pieces.map((p, order): ReadItem => {
      const biggest = cover && coverTitleOf(p.lines, cover, settings.local);
      const titleLine =
        biggest?.line ??
        p.lines.reduce((best, l) => (l.confidence * l.text.length > best.confidence * best.text.length ? l : best));
      // A cover's creator is only the name the name rule found (an author printed bigger than the title). The lines left over are noise, and
      // the server weighs a read's creator against each candidate's, so a wrong one costs a right title. A shelf piece guesses as it always did.
      const creator = cover ? biggest?.creator?.text : creatorOf(p.lines, titleLine, settings.local);
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
    // The lines and the pieces made of them are for the eval record only: nothing in the scanner reads them.
    const ocr = { lines, pieces: pieces.map((p) => p.lines.map((l) => lines.indexOf(l))) };
    return { items, unreadableCount, engine: this.name, ocr, timings: { totalMs: total, visionMs: total } };
  }
}
