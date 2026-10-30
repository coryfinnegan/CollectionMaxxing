// A single cover's title is its biggest text, not its longest line: the longest line on a cover is nearly always a blurb.
import type { TextLine } from '@/native/cm-vision';
import coverReads from './coverReads.json';
import { coverTitleOf, isBoilerplate } from './coverTitle';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, testIds, type Scene } from './fakes';
import type { Still } from './types';
import { VisionPipelineEngine } from './VisionPipelineEngine';

// A square page, so a box's shares are its pixels over 1000: a height of 0.1 is letters 100 pixels tall.
const PAGE = { width: 1000, height: 1000 };
const titleOf = (lines: TextLine[], page = PAGE) => coverTitleOf(lines, page, T)?.line.text;

describe('the title of a single cover', () => {
  it('is the line with the biggest letters, not the longest one', () => {
    const lines = [line('The most thrilling ride of the whole summer', 0.1, 0.05, 0.8, 0.03), line('JAWS', 0.3, 0.3, 0.4, 0.2)];
    expect(titleOf(lines)).toBe('JAWS');
  });

  it('measures letters by the smaller side of the box, so a sideways title counts by its width', () => {
    const tagline = line('SOMETHING WICKED', 0.1, 0.1, 0.8, 0.06);
    expect(titleOf([tagline, line('WING', 0.5, 0.2, 0.09, 0.4)])).toBe('WING');
    expect(titleOf([tagline, line('WING', 0.5, 0.2, 0.04, 0.4)])).toBe('SOMETHING WICKED');
  });

  it('measures a box in pixels of the picture, not in shares of it', () => {
    // On a landscape shot the sideways line is the smaller share of the frame (0.06 against 0.1) but the taller letters (120 pixels against 100).
    const lines = [line('WIDE', 0.1, 0.1, 0.5, 0.1), line('TALL', 0.8, 0.1, 0.06, 0.5)];
    expect(titleOf(lines, { width: 2000, height: 1000 })).toBe('TALL');
    expect(titleOf(lines, { width: 1000, height: 1000 })).toBe('WIDE');
  });

  it('uses the boxes as they are when the picture size is unknown', () => {
    const lines = [line('SMALL', 0.1, 0.1, 0.3, 0.05), line('BIG', 0.1, 0.5, 0.3, 0.15)];
    expect(titleOf(lines, { width: 0, height: 0 })).toBe('BIG');
  });

  it('joins the lines of a title broken over several lines, in reading order', () => {
    const slept = line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1);
    const camp = line('CAMP 3', 0.2, 0.75, 0.38, 0.11);
    const tagline = line('She found her way back!', 0.25, 0.26, 0.23, 0.06);
    const title = coverTitleOf([camp, tagline, slept], PAGE, T);
    expect(title?.line.text).toBe('SLEEP AWAY CAMP 3');
    expect(title?.used).toEqual(expect.arrayContaining([slept, camp]));
    expect(title?.used).not.toContain(tagline);
  });

  it('joins a line beside the biggest one on the same row', () => {
    expect(titleOf([line('THE', 0.1, 0.4, 0.09, 0.12), line('DARK', 0.2, 0.4, 0.2, 0.12), line('Hardcover edition', 0.1, 0.8, 0.2, 0.03)])).toBe('THE DARK');
  });

  it('stops at a line with much smaller letters, such as a subtitle', () => {
    expect(titleOf([line('WING COMMANDER', 0.1, 0.3, 0.8, 0.2), line('The Price of Freedom', 0.1, 0.5, 0.5, 0.08)])).toBe('WING COMMANDER');
  });

  it('stops at a line more than a letter height away', () => {
    expect(titleOf([line('FIRST', 0.1, 0.1, 0.4, 0.1), line('SECOND', 0.1, 0.5, 0.4, 0.1)])).toBe('FIRST');
  });

  it('stops where a smaller line stands between two big ones', () => {
    const lines = [line('GREAT', 0.1, 0.3, 0.4, 0.1), line('and also', 0.1, 0.4, 0.2, 0.03), line('EXPECTATIONS', 0.1, 0.43, 0.6, 0.1)];
    expect(titleOf(lines)).toBe('GREAT');
  });

  it('reads a line that Vision saw twice once, by the surer reading', () => {
    const sure = line('CAMP 3', 0.2, 0.75, 0.38, 0.11, 0.9);
    const unsure = line('CAMP 8', 0.2, 0.745, 0.39, 0.115, 0.5);
    const title = coverTitleOf([unsure, sure], PAGE, T);
    expect(title?.line.text).toBe('CAMP 3');
    // The repeated reading is the same title, so it is not a candidate for anything else (the creator, say).
    expect(title?.used).toEqual(expect.arrayContaining([sure, unsure]));
  });

  it('prefers the Latin reading when two readings are equally sure (Vision reads a P as a Cyrillic Ф)', () => {
    expect(titleOf([line('ФINOCCHIO', 0.22, 0.157, 0.58, 0.116), line('PINOCCHIO', 0.22, 0.158, 0.58, 0.113)])).toBe('PINOCCHIO');
  });

  it('does not trust the size of a slanted line, whose box is far squarer than its text is long', () => {
    const slanted = line('THE HUNT FOR RED OCTOBER', 0.04, 0, 0.22, 0.25);
    expect(titleOf([slanted, line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1)])).toBe('SLEEP AWAY');
  });

  it('gives nothing when every line is something a cover prints beside the title', () => {
    expect(coverTitleOf([line('#1 New York Times bestseller', 0.1, 0.1, 0.8, 0.1), line('A NOVEL', 0.4, 0.8, 0.2, 0.05)], PAGE, T)).toBeUndefined();
    expect(coverTitleOf([], PAGE, T)).toBeUndefined();
  });

  it('keeps a title that only contains a word a format logo uses', () => {
    expect(titleOf([line('DIGITAL FORTRESS', 0.1, 0.3, 0.8, 0.12), line('Dan Brown', 0.3, 0.8, 0.4, 0.05)])).toBe('DIGITAL FORTRESS');
  });
});

describe('what a cover prints beside its title', () => {
  it.each([
    '#1 New York Times bestseller',
    'HI NEW YORK TIMES BESTSELLER™',
    'NATIONAL BESTSELLER',
    'bestselling author of',
    '"Breathtaking',
    'special effects!"',
    '"This is Backman at the height of his empathy and resonance."',
    '-NPR',
    '—Joel Siegel,',
    '- Joyce Carol',
    'A NOVEL',
    'a Memoir',
    'From the best-selling novel by the author of JURASSIC PARK',
    'Author of A Winter at the Lighthouse',
    'DVD',
    'D DOLBY',
    'DOLBY DIGITAL',
    'DIGITAL',
    'Hi-Fi',
    'BLU-RAY DISC',
    'VHS',
    'THE CRITERION COLLECTION',
    'LICENSED BY Nintendo',
    'MADE IN JAPAN',
    'ASSEMBLED IN MEXICO',
    'E PlayStationlie',
    'SUPER NINTENDO.',
  ])('is not a title: %s', (text) => {
    expect(isBoilerplate(text)).toBe(true);
  });

  it.each([
    'CONGO',
    'Theo of Golden',
    'SLEEP AWAY',
    'The Hunt for Red October',
    'DIGITAL FORTRESS',
    "Angela's Ashes",
    'Strangers',
    'Belle Burden',
    'Erich Segal',
    'My Fofoter »', // an arrow Vision drew from the art is not a closing quote
  ])(
    'can be a title: %s',
    (text) => {
      expect(isBoilerplate(text)).toBe(false);
    },
  );
});

// Vision's own reading of twelve golden single covers, as the eval recorded it: the lines with their boxes (normalized, as the engine sees them) and the
// pixel size of the photo they came from. The record has no confidences, so every line gets the same one. Only text is kept here, never a photo.
interface RecordedCover {
  width: number;
  height: number;
  lines: { text: string; x: number; y: number; width: number; height: number }[];
}
const covers = coverReads as Record<string, RecordedCover>;

async function readCover(id: string, shotKind: 'single_cover' | 'shelf_segment' = 'single_cover') {
  const cover = covers[id];
  const still: Still = { uri: `fixture://${id}`, width: cover.width, height: cover.height };
  const scene: Scene = { text: cover.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height)) };
  const engine = new VisionPipelineEngine({ vision: new RecordingVision({ [still.uri]: scene }), settings: () => S, newId: testIds });
  return engine.read(still, () => {}, { shotKind });
}

describe('the Vision engine on twelve real covers', () => {
  it('has the twelve covers', () => {
    expect(Object.keys(covers)).toHaveLength(12);
  });

  // The title Vision's lines give, where they give it whole.
  it.each([
    ['bluray-01', 'RUSHMORE'],
    ['dvd-01', 'SLEEP AWAY CAMP 3'],
    ['dvd-02', 'CONGO'],
    ['game-box-01', 'WING CONNANDER'],
    ['paperback-03', 'Theo of Golden'],
    ['paperback-04', 'Strangers'],
    ['vhs-02', 'PINOCCHIO'],
  ])('reads %s as %s', async (id, title) => {
    const reading = await readCover(id);
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title).toBe(title);
  });

  // The lines cannot give these: a garbled cartridge label, a title Vision read as "Stórýßoök", a title split across "RED" and "OCTOBER",
  // and an author whose name is set in bigger letters than the title (FREDRIK BACKMAN over MY FRIENDS). The engine must still not take a blurb.
  const BLURB = /best.?sell|new york times|novel|licensed|assembled|made in|nintendo|playstation|npr|charming|monaghan|dolby|criterion|author of|siegel|breathtaking/i;

  it.each(Object.keys(covers))('does not take a blurb for the title of %s', async (id) => {
    const reading = await readCover(id);
    expect(reading.items[0].title).not.toMatch(BLURB);
  });

  it.each(['game-cartridge-01', 'game-cartridge-02', 'paperback-01', 'paperback-02', 'vhs-01'])('still gives %s one item with some title', async (id) => {
    const reading = await readCover(id);
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title.length).toBeGreaterThan(0);
  });

  it('keeps every line in the item text, whatever the title is', async () => {
    const reading = await readCover('dvd-02');
    expect(reading.items[0].text).toContain('JURASSIC PARK');
    expect(reading.items[0].text).toContain('Joel Siegel');
  });
});

describe('the creator beside a cover title', () => {
  const readLines = async (lines: TextLine[]) => {
    const still: Still = { uri: 'fixture://creator', ...PAGE };
    const engine = new VisionPipelineEngine({ vision: new RecordingVision({ [still.uri]: { text: lines } }), settings: () => S, newId: testIds });
    return (await engine.read(still, () => {}, { shotKind: 'single_cover' })).items[0];
  };

  it('is still the name-like line beside the title', async () => {
    const item = await readLines([line('JAWS', 0.3, 0.3, 0.4, 0.2), line('Peter Benchley', 0.3, 0.8, 0.4, 0.05)]);
    expect(item.title).toBe('JAWS');
    expect(item.creator).toBe('Peter Benchley');
  });

  it('is never a line of the title itself, nor a second reading of one', async () => {
    const title = [line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1), line('CAMP 3', 0.2, 0.75, 0.38, 0.11), line('CAMP B', 0.2, 0.745, 0.39, 0.115, 0.5)];
    const alone = await readLines(title);
    expect(alone.title).toBe('SLEEP AWAY CAMP 3');
    expect(alone.creator).toBeUndefined();
    const withAuthor = await readLines([...title, line('Jane Roe', 0.3, 0.9, 0.3, 0.04)]);
    expect(withAuthor.creator).toBe('Jane Roe');
  });
});

describe('a shelf shot still picks each piece title as it did: the most confident of the longest lines', () => {
  // Two spines. On the first the short line is the bigger; on the second the longer line is the bigger but the unsure one. The shelf rule
  // (confidence times length) takes the long tagline on the first and the short sure line on the second: size is not what it goes by.
  const spines: Scene = {
    text: [
      line('A very long tagline set in tiny type', 0.05, 0.1, 0.4, 0.03),
      line('BIG', 0.1, 0.4, 0.2, 0.08),
      line('Longer but unsure', 0.53, 0.2, 0.44, 0.14, 0.35),
      line('Short sure', 0.6, 0.6, 0.2, 0.05, 0.95),
    ],
    rectangles: [
      { x: 0.02, y: 0.05, width: 0.46, height: 0.8 },
      { x: 0.52, y: 0.05, width: 0.46, height: 0.8 },
    ],
  };
  const spineStill: Still = { uri: 'fixture://shelf', ...PAGE };
  const readSpines = (options: Parameters<VisionPipelineEngine['read']>[2]) =>
    new VisionPipelineEngine({ vision: new RecordingVision({ [spineStill.uri]: spines }), settings: () => S, newId: testIds }).read(spineStill, () => {}, options);

  it.each([['shelf_segment' as const], ['unknown' as const], [undefined]])('on a %s shot', async (shotKind) => {
    const reading = await readSpines(shotKind === undefined ? {} : { shotKind });
    expect(reading.items.map((i) => i.title)).toEqual(['A very long tagline set in tiny type', 'Short sure']);
  });

  it('while the same lines read as one cover take the biggest letters', async () => {
    const reading = await readSpines({ shotKind: 'single_cover' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title).toBe('Longer but unsure');
  });
});
