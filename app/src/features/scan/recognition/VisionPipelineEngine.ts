// The fallback engine: Vision text and rectangles, split in TypeScript, no model. Its titles are guesses, so every
// item says low confidence and leaves the finding to the server.
import type { DetectRectanglesOptions, Rect, TextLine, VisionEngine } from '@/native/cm-vision';
import { isSingleItemShot } from '../shotkind/shotKind';
import type { CueKind } from './coverKind';
import { coverTitleOf, type CoverTitle } from './coverTitle';
import { creatorOf } from './creators';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING, DEFAULT_MULTI_COVER_TUNING, type RecognitionSettings } from './defaults';
import { readLines } from './lines';
import { multipleCovers } from './multiCover';
import { productCodeOf } from './productCode';
import { shelfKind } from './shelfKind';
import { shelfTitleOf } from './shelfTitle';
import { splitIntoPieces, wholeFrame, type Piece, type Split } from './splitting';
import type { ReadItem, ReadOptions, RecognitionEngine, SnapReading, Still } from './types';

export interface EngineDeps {
  vision: VisionEngine;
  settings: () => RecognitionSettings;
  now?: () => number;
  newId?: () => string;
  /** What print says about a kind and a format; `DEFAULT_COVER_FORMAT_TUNING` unless the selector or a test gives its own. */
  coverFormatTuning?: CoverFormatTuning;
}

/** What a single cover's piece was read as: its title (nothing when no line is left to be one), and the kind its own text names. */
interface CoverRead {
  title: CoverTitle | undefined;
  kind?: CueKind;
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
    const single = isSingleItemShot(options.shotKind);
    // Both rectangle searches start with the text read, so looking for covers costs no extra time. A cover shot never looks: it is one item.
    const looking = single ? undefined : Promise.all([this.rectanglesOf(still), this.rectanglesOf(still, { maximumAspectRatio: DEFAULT_MULTI_COVER_TUNING.maxAspectRatio })]);
    const lines = await readLines(this.deps.vision, still.uri, settings.local, { shelf: !single });
    // A cover shot is one item: the whole frame, never segmented (#504), and its title is its biggest text. Anything else may be a shelf row.
    if (single) {
      const whole = wholeFrame(lines);
      return this.reading(whole, lines, settings, started, onItem, new Map(whole.pieces.map((p) => [p, { title: coverTitleOf(p.lines, still, settings.local) }])));
    }
    // The splitter gets the default search's rectangles, as it always did; the covers are found in the wide search's, which it never sees.
    const [rectangles, wide] = (await looking) ?? [[], []];
    // Several covers lying side by side are an item each; whatever lies outside them is a shelf as it always was.
    const several = multipleCovers(lines, wide, still, settings.local);
    const rest = splitIntoPieces(several?.rest ?? lines, rectangles, settings.splitting, settings.local);
    // A cover goes in as a plain piece (its lines and box); its title and kind ride beside it, since a piece's own `title` is a stack band's text.
    const covers = (several?.covers ?? []).map((c) => ({ piece: { lines: c.lines, box: c.box } satisfies Piece, read: { title: c.title, kind: c.kind } }));
    const pieces = [...covers.map((c) => c.piece), ...rest.pieces];
    // What the shelf's own print says it is a shelf of (shelfKind.ts). A spine is a few words, so no item here names a kind; this says what
    // kind of shelf they are all on, when at least two of the pieces just made print it. The server lets it speak only when no read of the
    // snap names that kind (types.ts).
    const printKind = shelfKind(lines, pieces, this.deps.coverFormatTuning ?? DEFAULT_COVER_FORMAT_TUNING);
    const reading = this.reading(
      { pieces, unreadableCount: rest.unreadableCount },
      lines,
      settings,
      started,
      onItem,
      new Map(covers.map((c) => [c.piece, c.read])),
    );
    return printKind ? { ...reading, printKind } : reading;
  }

  /** One rectangle search: with no options, the splitter's; with `maximumAspectRatio`, as wide as a case (Vision's default would never return a game's). Never throws: no rectangles is a reading too. */
  private async rectanglesOf(still: Still, options?: DetectRectanglesOptions): Promise<Rect[]> {
    try {
      return await (options ? this.deps.vision.detectRectangles(still.uri, options) : this.deps.vision.detectRectangles(still.uri));
    } catch {
      return [];
    }
  }

  /** The pieces in `covers` are single covers: each takes its biggest text as the title, and the kind its own text names. Any other piece is a stretch of shelf, whose title is its longest, surest line. */
  private reading(split: Split, lines: TextLine[], settings: RecognitionSettings, started: number, onItem: (item: ReadItem) => void, covers: ReadonlyMap<Piece, CoverRead>): SnapReading {
    const { pieces, unreadableCount } = split;
    const items = pieces.map((p, order): ReadItem => {
      const cover = covers.get(p);
      const biggest = cover?.title;
      // A shelf piece's title is chosen in shelfTitle.ts: never the author line or a sticker, the reading the other
      // readings of its place agree with, and both rows of a title printed over two (#617).
      const shelf = cover ? undefined : shelfTitleOf(p.lines, settings.local);
      // A cover whose own rule found no title falls back to its surest, longest line, as it always did (#504 owns covers).
      const titleLine =
        biggest?.line ??
        shelf?.line ??
        p.lines.reduce((best, l) => (l.confidence * l.text.length > best.confidence * best.text.length ? l : best));
      // A cover's creator is only the name the name rule found (an author printed bigger than the title). The lines left over are noise, and
      // the server weighs a read's creator against each candidate's, so a wrong one costs a right title. A shelf piece guesses as it always did.
      const creator = cover ? biggest?.creator?.text : creatorOf(p.lines, titleLine, settings.local);
      // A shelf piece is one spine, and a PlayStation spine prints its disc's code; the server settles which game the code names. A cover's code
      // is the cover cue's (coverCue.ts), which keeps it off a book or a film that the cover's own text shows the item to be.
      const productCode = cover ? undefined : productCodeOf(p.lines);
      return {
        localId: this.newId(),
        text: p.lines.map((l) => l.text).join(' '),
        title: p.title ?? shelf?.text ?? titleLine.text,
        ...(creator ? { creator } : {}),
        ...(productCode ? { productCode } : {}),
        kind: cover?.kind ?? 'other',
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
