// A spine's title is its title, not its author, not a bestseller line and not a sticker (#617). Synthetic spines pin the
// rules; the recorded OCR lines of five shelf photos (shelfReads.json: text and boxes only, never pictures) say what the
// rules are worth, against the choice they replace.
import type { TextLine } from '@/native/cm-vision';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_CONFIG as C, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, STILL, testIds } from './fakes';
import { normalize } from './lines';
import { readsAsSticker, shelfTitleOf } from './shelfTitle';
import reads from './shelfReads.json';
import { splitIntoPieces } from './splitting';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const titleOf = (lines: TextLine[]) => shelfTitleOf(lines, T)?.text;
const creatorsOf = (lines: TextLine[]) => shelfTitleOf(lines, T)?.rest.map((l) => l.text);

/** A row printed down an upright spine: read sideways, so the box is tall and its width is the letter height. */
const row = (text: string, x: number, y: number, letters: number, length: number, confidence = 0.9) =>
  line(text, x, y, letters, length, confidence);

describe('a spine piece\'s title is never its author line or a sticker', () => {
  it('takes the title and not the author printed below it in smaller lettering, however long the name is', () => {
    const lines = [row('THE NIGHT CIRCUS', 0.1, 0.2, 0.05, 0.3), row('ERIN R. MORGENSTERN THE NIGHT CIRCUS', 0.115, 0.55, 0.025, 0.4)];
    expect(titleOf(lines)).toBe('THE NIGHT CIRCUS');
  });

  it.each([
    // Vision read the name twice and the title once, so agreement alone would have taken the name (the recorded
    // "THE LUCKIEST LADY IN LONDON" spine): lettering is compared across places, agreement only within one.
    ['THE SEVEN HUSBANDS OF EVELYN HUGO', 0.05, 'TAYLOR JENKINS REID', 0.025],
    ['The Nanny', 0.03, 'LANA FERGUSON', 0.022],
    ['CARRIE', 0.05, 'STEPHEN KING', 0.025], // the author at exactly half the title's lettering
    ['A Latte Like Love', 0.021, 'MICHELLE C. HARRIS', 0.016], // read clean once each: no agreement to go on
    ['1984', 0.05, 'GEORGE ORWELL', 0.025], // a title that is only a number is no sticker
    ['THE ROAD', 0.04, 'VINTAGE INTERNATIONAL', 0.02],
    ['BELOVED', 0.04, 'WINNER OF THE PULITZER PRIZE', 0.02],
  ])('takes %s over the smaller %s beside it, read once or twice', (title, big, other, small) => {
    const length = (text: string) => 0.1 + text.length / 200;
    const lines = [
      row(title, 0.3, 0.2, big, length(title)),
      row(other, 0.34, 0.45, small, length(other)),
      row(`${other} )`, 0.341, 0.452, small * 0.9, length(other)), // a second, damaged reading of the name
    ];
    expect(titleOf(lines)).toBe(title);
  });

  it('takes the title over an author line set in the same lettering as it', () => {
    const lines = [row('THE NIGHT CIRCUS', 0.1, 0.2, 0.05, 0.3), row('#1 NEW YORK TIMES BESTSELLING AUTHOR', 0.1, 0.55, 0.05, 0.45)];
    expect(titleOf(lines)).toBe('THE NIGHT CIRCUS');
  });

  it('takes the title over a sticker printed larger than it', () => {
    const lines = [row('WIDESCREEN', 0.1, 0.1, 0.09, 0.2), row('HALLOWEEN', 0.1, 0.4, 0.05, 0.25)];
    expect(titleOf(lines)).toBe('HALLOWEEN');
  });

  it('takes the title over a stock number and an edition sticker', () => {
    const lines = [
      row('10th ANNIVERSARY EDITION', 0.1, 0.1, 0.06, 0.3),
      row('Stock No. 038', 0.1, 0.5, 0.06, 0.2),
      row('EXCALIBUR', 0.1, 0.75, 0.05, 0.2),
    ];
    expect(titleOf(lines)).toBe('EXCALIBUR');
  });

  it('still says a sticker when the spine printed nothing else', () => {
    expect(titleOf([row('WIDESCREEN', 0.1, 0.1, 0.05, 0.2)])).toBe('WIDESCREEN');
  });

  it('keeps a title that reads as a name over a scrap of noise: most titles on a shelf read as names too', () => {
    const lines = [row('THEY LIVE', 0.1, 0.2, 0.05, 0.25), row('+T-', 0.1, 0.6, 0.045, 0.03)];
    expect(titleOf(lines)).toBe('THEY LIVE');
  });

  it('takes the biggest lettering, so a shelf label printed across the row is no spine title', () => {
    const lines = [line('JY WORLDS', 0, 0.005, 0.235, 0.055), row('THEY LIVE', 0.047, 0.46, 0.137, 0.165)];
    expect(titleOf(lines)).toBe('THEY LIVE');
  });

  it('says which lines read as a sticker and which do not', () => {
    const stickers = ['WIDESCREEN', 'Stock No. 038', '03928', "COLLECTOR'S", '10th ANNIVERSARY EDITION', "DIRECTOR'S CUT", 'SPECIAL EDITION', 'Vol 2'];
    for (const sticker of stickers) expect([sticker, readsAsSticker(sticker, T)]).toEqual([sticker, true]);
    // A number says nothing either way, and nearly every sticker word is somebody's title on its own.
    const titles = ['HALLOWEEN', 'Full Metal Jacket', 'A Court of Thorns and Roses', 'Charlotte\'s Web', 'EXCALIBUR', '1984', 'SE7EN', 'GREMLINS2', '300', 'Used', 'Special', 'Side', 'Cut'];
    for (const title of titles) expect([title, readsAsSticker(title, T)]).toEqual([title, false]);
  });
});

describe('when the same words were read several times in one place', () => {
  it('takes the reading the other readings agree on, not the longest one', () => {
    // Vision read this spine three times: the title alone twice, and once with the author run onto the end of it.
    const lines = [
      row('A Latte Like Love', 0.172, 0.179, 0.021, 0.147),
      row('A Latte (ike Love ))) MICHELLE C. HARRIS', 0.174, 0.179, 0.027, 0.308, 0.5),
      row('A Latte Like Love 1)', 0.172, 0.181, 0.022, 0.165),
      row('MICHELLE C. HARRIS', 0.184, 0.35, 0.016, 0.112),
    ];
    expect(titleOf(lines)).toBe('A Latte Like Love');
  });

  it('does not let the author\'s name, read inside a reading of title and author at once, count as a reading of the title', () => {
    const lines = [
      row('FLAWLESS', 0.288, 0.21, 0.025, 0.136),
      row('FLAWLESS ELSIE SILVER', 0.29, 0.21, 0.029, 0.239),
      row('ELSIE SILVER', 0.298, 0.362, 0.019, 0.089),
    ];
    expect(titleOf(lines)).toContain('FLAWLESS');
  });

  it('falls back to the surest, longest reading when no two readings agree', () => {
    const lines = [row('GOOD FEAIS', 0.555, 0.35, 0.082, 0.35), row('GOOD TEATS', 0.556, 0.352, 0.075, 0.341, 0.5)];
    expect(titleOf(lines)).toBe('GOOD FEAIS');
  });
});

describe('a title printed over two rows of one spine', () => {
  it('reads both rows as one title, top row first', () => {
    const lines = [row('GREMLINS 2', 0.614, 0.26, 0.034, 0.139), row('THE NEW BATCH', 0.6, 0.266, 0.021, 0.113)];
    expect(titleOf(lines)).toBe('GREMLINS 2 THE NEW BATCH');
  });

  it('leaves out a row set much smaller than the chosen one', () => {
    const lines = [row('FREE WILLY 3', 0.737, 0.277, 0.054, 0.154), row('the rescue', 0.74, 0.267, 0.014, 0.146)];
    expect(titleOf(lines)).toBe('FREE WILLY 3');
  });

  it('leaves out a row further away than one letter height: that is the next spine', () => {
    const lines = [row('THE RETURN OF THE KING', 0.614, 0.26, 0.034, 0.3), row('THE NEW BATCH', 0.5, 0.266, 0.031, 0.113)];
    expect(titleOf(lines)).toBe('THE RETURN OF THE KING');
  });

  it('never joins another reading of the row it chose', () => {
    const lines = [row('IT HAPPENED ONE SUMMER', 0.5, 0.2, 0.03, 0.2), row('IT HAPPENED ONE SUMMER Tessa Bailey', 0.502, 0.2, 0.033, 0.28)];
    expect(titleOf(lines)).toBe('IT HAPPENED ONE SUMMER');
  });

  it('reads two rows of a label lying flat downwards', () => {
    const lines = [line('HOW STELLA GOT', 0.024, 0.468, 0.224, 0.033), line('HER GROOVE BACK', 0.073, 0.49, 0.235, 0.032)];
    expect(titleOf(lines)).toBe('HOW STELLA GOT HER GROOVE BACK');
  });

  it.each([
    ['sits clear of it', 0.566],
    ['only grazes it: a block of print runs into itself, two spines do not', 0.571],
  ])('does not join the next spine along, which %s', (_what, x) => {
    const lines = [row('THE SHINING', 0.6, 0.2, 0.03, 0.16), row('CARRIE', x, 0.21, 0.03, 0.15)];
    expect(titleOf(lines)).toBe('THE SHINING');
  });

  it('does not join a label lying flat that only grazes the chosen one', () => {
    const lines = [line('HOW STELLA GOT', 0.024, 0.468, 0.224, 0.033), line('NEXT TAPE', 0.03, 0.5005, 0.22, 0.033)];
    expect(titleOf(lines)).toBe('HOW STELLA GOT');
  });

  it('does not join a row that only runs beside part of the chosen one', () => {
    // The second spine's text sits inside the chosen row's span but covers barely half of it: not a row of this title.
    const lines = [row('SLEEPAWAY CAMP 3 TEENAGE WASTELAND', 0.283, 0.423, 0.134, 0.32), row('TOTAL RECALL', 0.208, 0.527, 0.087, 0.123)];
    expect(titleOf(lines)).toBe('SLEEPAWAY CAMP 3 TEENAGE WASTELAND');
  });

  it('says nothing for a piece with no line in it', () => {
    expect(shelfTitleOf([], T)).toBeUndefined();
  });
});

describe('what is left over for the author-finder', () => {
  it('never offers the title\'s own second row as the author', async () => {
    const lines = [row('THE HUNGER', 0.6, 0.2, 0.03, 0.12), row('GAMES', 0.5755, 0.205, 0.029, 0.11)];
    expect(titleOf(lines)).toBe('THE HUNGER GAMES');
    expect(creatorsOf(lines)).toEqual([]);
  });

  it('never offers a sticker as the author', async () => {
    const lines = [row('HALLOWEEN', 0.1, 0.4, 0.05, 0.25), line('WIDESCREEN', 0.1, 0.1, 0.09, 0.02)];
    expect(creatorsOf(lines)).toEqual([]);
  });

  it('still offers the name beside the title', async () => {
    const lines = [row('THE NIGHT CIRCUS', 0.1, 0.2, 0.05, 0.3), row('ERIN MORGENSTERN', 0.115, 0.55, 0.025, 0.2)];
    expect(creatorsOf(lines)).toEqual(['ERIN MORGENSTERN']);
  });
});

describe('real shelf photos', () => {
  type Truth = { title: string; alsoTitled?: string[] };
  type Photo = { truth: Truth[]; lines: { text: string; x: number; y: number; width: number; height: number }[] };
  const photos = reads as Record<string, Photo>;
  const titles = (t: Truth) => [t.title, ...(t.alsoTitled ?? [])];
  const holds = (text: string, wanted: string) => ` ${normalize(text)} `.includes(` ${normalize(wanted)} `);

  /**
   * The recorded lines kept no confidence. Vision is surer of a clean reading than of noise from text read the wrong way up,
   * so a line of plain ASCII stands in at 0.9 and any other at 0.5, the same for the choice before and the choice after.
   */
  const confidence = (text: string) => (/^[\x20-\x7E]+$/.test(text) ? 0.9 : 0.5);
  const linesOf = (photo: Photo) => photo.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));

  /** The titles the pipeline reads today: shelfTitle.ts for every stretch of shelf. */
  async function after(id: string): Promise<string[]> {
    const vision = new RecordingVision({ [STILL.uri]: { text: linesOf(photos[id]), rectangles: [] } });
    const engine = new VisionPipelineEngine({ vision, settings: () => S, newId: testIds });
    const { items } = await engine.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    return items.map((i) => i.title);
  }

  /** The choice shelfTitle.ts replaced: the piece's surest, longest single line, whatever that line said. */
  function before(id: string): string[] {
    const { pieces } = splitIntoPieces(linesOf(photos[id]), [], C.splitting, T);
    return pieces.map((p) => p.title ?? p.lines.reduce((b, l) => (l.confidence * l.text.length > b.confidence * b.text.length ? l : b)).text);
  }

  /** How many of the photo's real titles a list of read titles holds, and how many it says exactly. */
  const score = (photo: Photo, read: string[]) => ({
    held: photo.truth.filter((t) => titles(t).some((v) => read.some((r) => holds(r, v)))).length,
    exact: photo.truth.filter((t) => titles(t).some((v) => read.some((r) => normalize(r) === normalize(v)))).length,
  });

  // Today's counts on the five photos, the choice before and the choice after. A title is held when its words are inside a
  // read title and exact when a read title says it and nothing else; the server finds an exact title far more often (#617).
  const COUNTS: Record<string, { before: { held: number; exact: number }; after: { held: number; exact: number } }> = {
    // Three piles of tapes with a row of DVDs between them: nearly every title is a pile band, which joined its rows already.
    'vhs-dvd-shelf-01': { before: { held: 6, exact: 6 }, after: { held: 6, exact: 6 } },
    // Upright DVD spines under shelf labels and edition stickers: "THEY LIVE", "DUNE" and "HALLOWEEN" come out instead.
    'vhs-dvd-shelf-02': { before: { held: 3, exact: 3 }, after: { held: 5, exact: 5 } },
    // Romance paperback spines, each with the author beside the title: more titles said on their own, and three held
    // titles traded for two. All five of those sit in pieces that hold several books' lines rather than one spine, where
    // the biggest print belongs to another book than the one that was found before. No rule that reads one piece as one
    // item can see that; the split owns it (splitting.ts).
    'accept-shelf-04': { before: { held: 20, exact: 7 }, after: { held: 19, exact: 9 } },
    'accept-shelf-01': { before: { held: 14, exact: 6 }, after: { held: 14, exact: 7 } },
    'accept-shelf-02': { before: { held: 15, exact: 10 }, after: { held: 15, exact: 12 } },
  };

  it.each(Object.entries(COUNTS))('%s: the titles held and the titles said exactly, before and after', async (id, counts) => {
    expect(score(photos[id], before(id))).toEqual(counts.before);
    expect(score(photos[id], await after(id))).toEqual(counts.after);
  });

  it('says more titles exactly across the five photos than the choice it replaced, and holds no fewer', async () => {
    const sum = (pick: (c: { held: number; exact: number }) => number, which: 'before' | 'after') =>
      Object.values(COUNTS).reduce((n, c) => n + pick(c[which]), 0);
    expect(sum((c) => c.exact, 'after')).toBeGreaterThan(sum((c) => c.exact, 'before'));
    expect(sum((c) => c.held, 'after')).toBeGreaterThanOrEqual(sum((c) => c.held, 'before'));
  });

  it('never says an author\'s name alone where a spine printed a title too', async () => {
    const read = await after('accept-shelf-01');
    for (const author of ['DEB PERELMAN', 'FREDERICKSON', 'ALTON BROWN', 'BRYAN VOLTAGGIO']) {
      expect([author, read]).not.toContainEqual([author, expect.arrayContaining([author])]);
    }
  });
});
