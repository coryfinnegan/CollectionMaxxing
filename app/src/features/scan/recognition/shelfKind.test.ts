// What the printed text of a whole shelf says about the kind of shelf it is: a film shelf (a disc or tape format, the aspect the
// picture is in, a running time), a game shelf (a console's name), a book shelf (a bestseller line). Mixed print, or none, says
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
  ])('reads %s as a film shelf', (_why, text) => {
    expect(kindOf('SOME SPINE', text, 'ANOTHER SPINE')).toBe('film');
  });

  it('reads the Criterion Collection on a spine as a film shelf, as a single cover does', () => {
    expect(kindOf('THE CRITERION COLLECTION', 'A SPINE')).toBe('film');
  });

  it.each([
    ['a title that ends in those letters', 'SAMIN'],
    ['a year, which is not a running time', '1999'],
    ['a page count, which no film prints', '352 pages'],
    ['a plain shelf of spines', 'THE SECRET HISTORY'],
  ])('finds nothing in %s', (_why, text) => {
    expect(kindOf('A SPINE', text, 'ANOTHER SPINE')).toBeUndefined();
  });
});

describe('shelfKind: a console makes a game shelf and a bestseller line a book shelf', () => {
  it('reads a console name as a game shelf', () => {
    expect(kindOf('PlayStation 2', 'GRAN TURISMO 3')).toBe('game');
  });

  it('reads a bestseller line as a book shelf', () => {
    expect(kindOf('A NOVEL', 'THE SECRET HISTORY')).toBe('book');
  });
});

describe('shelfKind: print that disagrees says nothing', () => {
  it('says nothing when a film format and a bestseller line are both printed', () => {
    expect(kindOf('DVD', 'A NOVEL')).toBeUndefined();
  });

  it('says nothing when a console and a film format are both printed', () => {
    expect(kindOf('NINTENDO 64', 'VHS')).toBeUndefined();
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
    text: [line('STAR TREK GENERATIONS', 0.1, 0.1, 0.05, 0.6), line('92 Minutes Color', 0.2, 0.1, 0.05, 0.6)],
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
