// What the printed text of a whole shelf says about the kind of shelf it is: a film shelf (a disc or tape format, the aspect the
// picture is in, a running time), a game shelf (a console's name), a book shelf (a bestseller line). One line is one spine, and a
// shelf's kind is printed on several of its spines, so the print must name it on at least two lines. Mixed print, or none, says
// nothing. The recorded shelf reads are the real photos the eval run measures.
import type { TextLine } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS } from './defaults';
import { EngineSelector } from './EngineSelector';
import { line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { shelfKind } from './shelfKind';
import shelfReads from './shelfReads.json';
import { VisionPipelineEngine } from './VisionPipelineEngine';

/** Spines of text down a shelf; only their words matter to a print cue. */
const said = (...texts: string[]): TextLine[] => texts.map((t, i) => line(t, 0.1 + i * 0.05, 0.1, 0.04, 0.8));
const kindOf = (...texts: string[]) => shelfKind(said(...texts));
/** The same print on two spines among others: what a shelf of one kind looks like. */
const onTwoSpines = (text: string) => kindOf('SOME SPINE', text, 'ANOTHER SPINE', text);

interface RecordedShelf {
  lines: { text: string; x: number; y: number; width: number; height: number }[];
}
const recorded = shelfReads as unknown as Record<string, RecordedShelf>;
const fromRecording = (id: string): TextLine[] => recorded[id].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));

describe('shelfKind: film print makes a film shelf', () => {
  it.each([
    ['a disc format', 'DVD'],
    ['a tape format', 'VHS'],
    ['a Blu-ray', 'BLU-RAY'],
    ['a Blu-ray OCR split in two', 'Blu ray'],
    ['Ultra HD', 'ULTRA HD'],
    ['the aspect the picture is in', 'WIDESCREEN'],
    ['the other aspect', 'FULL SCREEN'],
    ['a letterboxed tape', 'LETTERBOXED'],
    ['a running time', '92 Minutes'],
    ['a running time beside the color line', '90 Minutes/Color'],
    ['a short running time', '47 min'],
    ['a running time in the short form', '98 mins'],
    ['a running time as it is printed', '90 MINUTES'],
  ])('reads %s on two spines as a film shelf', (_why, text) => {
    expect(onTwoSpines(text)).toBe('film');
  });

  it('counts a film format on one spine and a running time on another as two spines of film print', () => {
    expect(kindOf('SOME SPINE', 'DVD', 'ANOTHER SPINE', '92 Minutes')).toBe('film');
  });

  it('reads the Criterion Collection on two spines as a film shelf, as a single cover does', () => {
    expect(onTwoSpines('THE CRITERION COLLECTION')).toBe('film');
  });

  it.each([
    ['a disc format', 'DVD'],
    ['a running time', '92 Minutes'],
    ['the Criterion Collection', 'THE CRITERION COLLECTION'],
  ])('says nothing when %s is printed on one spine only, since one line is one spine', (_why, text) => {
    expect(kindOf('SOME SPINE', text, 'ANOTHER SPINE')).toBeUndefined();
  });

  it('says nothing for a book shelf with one 30-Minute Meals spine among plain book titles', () => {
    expect(kindOf('THE SECRET HISTORY', '30-Minute Meals', 'DUNE', 'EMMA')).toBeUndefined();
  });

  it('says nothing for a book shelf with one 92 Minutes line, since one line is one spine', () => {
    expect(kindOf('THE SECRET HISTORY', '92 Minutes', 'DUNE', 'EMMA')).toBeUndefined();
  });

  it('keeps a Criterion name split in two lines of one spine as one spine: it vetoes but adds no film line', () => {
    expect(kindOf('SOME SPINE', 'CRITERION', 'COLLECTION', 'DVD')).toBeUndefined();
    expect(kindOf('CRITERION', 'COLLECTION', 'PlayStation 2', 'PlayStation 2')).toBeUndefined();
  });

  it.each([
    ['a title that ends in those letters', 'SAMIN'],
    ['a year, which is not a running time', '1999'],
    ['a page count, which no film prints', '352 pages'],
    ['a plain shelf of spines', 'THE SECRET HISTORY'],
    ['a title with a singular minute and a hyphen', '30-Minute Meals'],
    ['a title with a singular minute and a space', '30 Minute Meals'],
    ['a title that counts one minute', '5-Minute Bedtime Stories'],
    ['a title with a number and a minute', '4 Minute Mile'],
  ])('finds nothing in %s, even on two spines', (_why, text) => {
    expect(onTwoSpines(text)).toBeUndefined();
  });
});

describe('shelfKind: a console makes a game shelf and a bestseller line a book shelf', () => {
  it('reads a console name on two spines as a game shelf', () => {
    expect(kindOf('PlayStation 2', 'GRAN TURISMO 3', 'NINTENDO 64')).toBe('game');
  });

  it('reads a bestseller line on two spines as a book shelf', () => {
    expect(kindOf('A NOVEL', 'THE SECRET HISTORY', 'A NOVEL')).toBe('book');
  });

  it('says nothing for a console name or a bestseller line on one spine', () => {
    expect(kindOf('PlayStation 2', 'GRAN TURISMO 3')).toBeUndefined();
    expect(kindOf('A NOVEL', 'THE SECRET HISTORY')).toBeUndefined();
  });
});

describe('shelfKind: print that disagrees says nothing', () => {
  it('says nothing when a film format and a bestseller line are both printed, though each is on two spines', () => {
    expect(kindOf('DVD', 'A NOVEL', 'DVD', 'A NOVEL')).toBeUndefined();
  });

  it('says nothing when a console and a film format are both printed, though each is on two spines', () => {
    expect(kindOf('NINTENDO 64', 'VHS', 'NINTENDO 64', 'VHS')).toBeUndefined();
  });

  it('says nothing for a shelf with no print of any kind', () => {
    expect(kindOf('THE SECRET HISTORY', 'DONNA TARTT')).toBeUndefined();
  });

  it('says nothing for no lines at all', () => {
    expect(shelfKind([])).toBeUndefined();
  });
});

describe('shelfKind: the recorded shelf photos', () => {
  it.each([['vhs-dvd-shelf-01'], ['vhs-dvd-shelf-02']])('reads %s as a film shelf', (id) => {
    expect(shelfKind(fromRecording(id))).toBe('film');
  });

  it.each([['accept-shelf-01'], ['accept-shelf-02'], ['accept-shelf-04']])('leaves %s with no kind, as today', (id) => {
    expect(shelfKind(fromRecording(id))).toBeUndefined();
  });
});

describe('the engines send the shelf kind with the snap', () => {
  const shelf: Scene = {
    read: okRead([modelItem('Dune', 'Dune', 0, { kind: 'book' })]),
    extract: okRead([modelItem('Dune', 'Dune', 0, { kind: 'book' })]),
    text: [
      line('STAR TREK GENERATIONS', 0.1, 0.1, 0.05, 0.6),
      line('92 Minutes Color', 0.2, 0.1, 0.05, 0.6),
      line('90 Minutes/Color', 0.3, 0.1, 0.05, 0.6),
    ],
    rectangles: [{ x: 0.08, y: 0.05, width: 0.09, height: 0.7 }],
  };
  const deps = (scene: Scene) => {
    const vision = new RecordingVision({ [STILL.uri]: scene });
    return { vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds };
  };

  it('the Vision tier reads the shelf kind from the print and still names no kind on any spine', async () => {
    const reading = await new VisionPipelineEngine(deps(shelf)).read(STILL, () => {}, { shotKind: 'shelf_segment' });

    expect(reading.printKind).toBe('film');
    expect(reading.items.map((i) => i.kind)).toEqual(reading.items.map(() => 'other'));
  });

  it('a cover shot has no shelf kind: its print speaks for the one item on it', async () => {
    const reading = await new VisionPipelineEngine(deps(shelf)).read(STILL, () => {}, { shotKind: 'single_cover' });

    expect(reading.printKind).toBeUndefined();
  });

  it('the combined shelf reading keeps the shelf kind Vision read, though the items are the model’s', async () => {
    const { vision } = deps(shelf);
    const reading = await new EngineSelector({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }).read(STILL, () => {}, {
      shotKind: 'shelf_segment',
    });

    expect(reading.engine).toBe('apple_model');
    expect(reading.printKind).toBe('film');
  });

  it('a shelf whose print says nothing sends no shelf kind', async () => {
    const plain: Scene = { ...shelf, text: [line('THE SECRET HISTORY', 0.1, 0.1, 0.05, 0.6)] };

    const reading = await new VisionPipelineEngine(deps(plain)).read(STILL, () => {}, { shotKind: 'shelf_segment' });

    expect(reading.printKind).toBeUndefined();
  });
});
