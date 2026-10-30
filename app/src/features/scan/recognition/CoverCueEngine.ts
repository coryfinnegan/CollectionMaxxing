// The cover cue as one piece that wraps any recognition engine. On a single-item shot the case's logos and proportions and the cover's printed
// text correct what an engine read (the film format a model gets wrong, the kind a weaker engine gets wrong: coverCue.ts). The picture model, the
// OCR-text tier and the Vision tier are each wrapped, so the live scanner and the eval runner (which reads each tier on its own) correct a cover
// the same way. A shelf shot passes straight through.
import type { Rect, VisionEngine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import { type CoverLook, type CueArrival, correctRead, coverCue, describeCue } from './coverCue';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING, type RecognitionSettings } from './defaults';
import { readLines } from './lines';
import type { ReadItem, ReadOptions, RecognitionEngine, SnapReading, Still } from './types';

export interface CoverCueDeps {
  vision: VisionEngine;
  settings: () => RecognitionSettings;
  /** The case's proportions and the cut-offs around them, and how long the cue may be waited for; `DEFAULT_COVER_FORMAT_TUNING` unless given. */
  coverFormatTuning?: CoverFormatTuning;
}

/**
 * The cue held by the one read. It is looked for while the engine reads; each item the engine streams waits for it (or for it to be given up
 * on) and goes out corrected, in the order it came. It never rejects, and never holds a read for longer than `waitMs` after the engine's
 * read ended. It also remembers how the wait ended, for the record the reading carries (`describeCue`).
 */
class PendingCue {
  private readonly look: Promise<CoverLook | undefined>;
  private emitting: Promise<void> = Promise.resolve();
  private giveUp: () => void = () => {};
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** `waiting` until the search answers, fails, or is given up on; whichever comes first settles it. */
  private state: 'waiting' | 'arrived' | 'timeout' | 'failed' = 'waiting';

  constructor(found: Promise<CoverLook | undefined>, private readonly waitMs: number) {
    this.look = new Promise((resolve) => {
      this.giveUp = () => {
        if (this.state === 'waiting') this.state = 'timeout';
        resolve(undefined);
      };
      found.then(
        (look) => {
          if (this.state === 'waiting') this.state = 'arrived';
          resolve(look);
        },
        () => {
          if (this.state === 'waiting') this.state = 'failed';
          resolve(undefined);
        },
      );
    });
    void this.look.then(() => clearTimeout(this.timer));
  }

  /** `onItem` for the engine: each item goes on once the cue is known, corrected by it, in order. */
  stream(onItem: (item: ReadItem) => void): (item: ReadItem) => void {
    return (item) => {
      const next = this.emitting.then(async () => onItem(await this.correct(item)));
      next.catch(() => {}); // a failing onItem surfaces when the read awaits `finish`, as it did when the engine emitted directly
      this.emitting = next;
    };
  }

  /**
   * The engine's read is over: from here the cue has `waitMs` to arrive. The streamed items have gone out and the reading's items carry the same
   * correction. The reading also says what the cue saw and whether it came in time (`describeCue`): for the eval record, ignored by the scanner.
   */
  async finish(reading: SnapReading): Promise<SnapReading> {
    const readyInTime = this.state === 'arrived';
    if (this.state === 'waiting' && this.timer === undefined) this.timer = setTimeout(this.giveUp, this.waitMs);
    await this.emitting;
    const look = await this.look;
    const arrival: CueArrival = look ? (readyInTime ? 'ready' : 'late') : this.state === 'failed' ? 'failed' : 'timeout';
    const cue = look?.cue;
    return { ...reading, ...(cue ? { items: reading.items.map((item) => correctRead(item, cue)) } : {}), coverCue: describeCue(arrival, look) };
  }

  /** The engine's read failed: whatever was streamed goes out as it is, now. */
  async abandon(): Promise<void> {
    this.giveUp();
    await this.emitting.catch(() => {});
  }

  private async correct(item: ReadItem): Promise<ReadItem> {
    const cue = (await this.look)?.cue;
    return cue ? correctRead(item, cue) : item;
  }
}

class CoverCuedEngine implements RecognitionEngine {
  constructor(
    private readonly engine: RecognitionEngine,
    private readonly deps: CoverCueDeps,
  ) {}

  get name(): RecognitionEngine['name'] {
    return this.engine.name;
  }

  isAvailable(): Promise<boolean> {
    return this.engine.isAvailable();
  }

  async read(still: Still, onItem: (item: ReadItem) => void, options: ReadOptions = {}): Promise<SnapReading> {
    if (!isSingleItemShot(options.shotKind)) return this.engine.read(still, onItem, options);
    const tuning = this.deps.coverFormatTuning ?? DEFAULT_COVER_FORMAT_TUNING;
    // The cover is looked at while the engine reads, so the cue is ready (or nearly) when the engine's items are.
    const cue = new PendingCue(this.findCue(still, tuning), tuning.cueWaitMs);
    try {
      return await cue.finish(await this.engine.read(still, cue.stream(onItem), options));
    } catch (error) {
      await cue.abandon();
      throw error;
    }
  }

  /** Rectangles and text, asked together. A failed rectangle search leaves the text cues; Vision text never throws (no text is no cue). */
  private async findCue(still: Still, tuning: CoverFormatTuning): Promise<CoverLook> {
    const [rectangles, lines] = await Promise.all([this.rectanglesOf(still, tuning), readLines(this.deps.vision, still.uri, this.deps.settings().local)]);
    const input = { still, rectangles, lines, tuning };
    return { input, cue: coverCue(input) };
  }

  /** The search is asked for shapes as wide as a case: Vision's default would never return a DVD or a Blu-ray one. */
  private async rectanglesOf(still: Still, tuning: CoverFormatTuning): Promise<Rect[]> {
    try {
      return await this.deps.vision.detectRectangles(still.uri, { maximumAspectRatio: tuning.maxCaseAspectRatio });
    } catch {
      return [];
    }
  }
}

/** The engine, with the cover cue applied to a single-item shot. It is the same engine to everything else: its name, its availability, its failures. */
export function withCoverCue(engine: RecognitionEngine, deps: CoverCueDeps): RecognitionEngine {
  return new CoverCuedEngine(engine, deps);
}
