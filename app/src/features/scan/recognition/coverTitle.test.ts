// A single cover's title is its biggest text, not its longest line: the longest line on a cover is nearly always a blurb.
import type { TextLine } from '@/native/cm-vision';
import coverReads from './coverReads.json';
import { coverTitleOf, isBoilerplate, isNameLike } from './coverTitle';
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
    const tagline = line('SOMETHING WICKED', 0.1, 0.1, 0.8, 0.05);
    expect(titleOf([tagline, line('WING', 0.5, 0.2, 0.09, 0.4)])).toBe('WING');
    expect(titleOf([tagline, line('WING', 0.5, 0.2, 0.02, 0.4)])).toBe('SOMETHING WICKED');
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

  it('joins a line set a little smaller than the biggest, as OCTOBER under RED is (two thirds of its height)', () => {
    expect(titleOf([line('RED', 0.4, 0.3, 0.2, 0.137), line('OCTOBER', 0.35, 0.44, 0.3, 0.09)])).toBe('RED OCTOBER');
  });

  it('joins a subtitle set at about two thirds of the title, which tells a sequel from its first film', () => {
    const lines = [line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1), line('CAMP 3', 0.2, 0.75, 0.38, 0.11), line('TEENAGE WASTELAND', 0.2, 0.86, 0.5, 0.072)];
    expect(titleOf(lines)).toBe('SLEEP AWAY CAMP 3 TEENAGE WASTELAND');
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

  it('prefers the reading with the fewest non-ASCII letters when two are equally sure (Vision reads an F as an accented E)', () => {
    expect(titleOf([line('ÉREDRIK', 0.26, 0.338, 0.48, 0.175), line('FREDRIK', 0.28, 0.372, 0.43, 0.127)])).toBe('FREDRIK');
    expect(titleOf([line('FREDRIK', 0.28, 0.372, 0.43, 0.127), line('ÉREDRIK', 0.26, 0.338, 0.48, 0.175)])).toBe('FREDRIK');
  });

  it('still takes the surer reading before it counts accents', () => {
    expect(titleOf([line('FREDRIK', 0.28, 0.372, 0.43, 0.127, 0.5), line('ÉREDRIK', 0.26, 0.338, 0.48, 0.175, 0.9)])).toBe('ÉREDRIK');
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

describe('a name, as an author is printed', () => {
  it.each(['FREDRIK BACKMAN', 'Charlotte Brontë', "Flannery O'Connor", 'JANE EYRE', 'Gabriel García Márquez', 'Jo Nesbø'])('is name-like: %s', (text) => {
    expect(isNameLike(text, T)).toBe(true);
  });

  it.each([
    ['one word', 'BACKMAN'],
    ['four words', 'THE LORD OF RINGS'],
    ['a function word', 'MY FRIENDS'],
    ['a function word in the middle', 'Theo of Golden'],
    ['a number', 'CAMP 3'],
    ['a one-letter word', 'J K Rowling'],
    ['a symbol', 'FHE PRICE*'],
    ['an arrow', 'My Fofoter »'],
  ])('is not name-like, because of %s: %s', (_why, text) => {
    expect(isNameLike(text, T)).toBe(false);
  });
});

describe('an author printed bigger than the title', () => {
  const author = [line('FREDRIK', 0.26, 0.1, 0.43, 0.13), line('BACKMAN', 0.19, 0.25, 0.6, 0.15)];

  it('leaves the title to the smaller run, and the name is the creator', () => {
    const title = coverTitleOf([...author, line('MY FRIENDS', 0.23, 0.7, 0.53, 0.11)], PAGE, T);
    expect(title?.line.text).toBe('MY FRIENDS');
    expect(title?.creator?.text).toBe('FREDRIK BACKMAN');
    expect(title?.used).toHaveLength(1);
  });

  it('takes the title wherever it stands, above the name too', () => {
    const title = coverTitleOf([line('MY FRIENDS', 0.23, 0.1, 0.53, 0.11), ...author.map((l) => ({ ...l, box: { ...l.box, y: l.box.y + 0.5 } }))], PAGE, T);
    expect(title?.line.text).toBe('MY FRIENDS');
    expect(title?.creator?.text).toBe('FREDRIK BACKMAN');
  });

  it('joins the lines of the smaller run into its title', () => {
    const lines = [...author, line('THE', 0.3, 0.7, 0.1, 0.11), line('SILENT SEA', 0.3, 0.8, 0.5, 0.1)];
    const title = coverTitleOf(lines, PAGE, T);
    expect(title?.line.text).toBe('THE SILENT SEA');
    expect(title?.creator?.text).toBe('FREDRIK BACKMAN');
  });

  it('keeps a title that is itself name-like, since the other run is name-like too', () => {
    const title = coverTitleOf([line('JANE EYRE', 0.1, 0.3, 0.8, 0.2), line('Charlotte Brontë', 0.2, 0.8, 0.5, 0.1)], PAGE, T);
    expect(title?.line.text).toBe('JANE EYRE');
    expect(title?.creator).toBeUndefined();
  });

  it('keeps the name when the smaller run is less than half its height', () => {
    const title = coverTitleOf([...author, line('A TALE OF TWO CITIES', 0.1, 0.8, 0.8, 0.06)], PAGE, T);
    expect(title?.line.text).toBe('FREDRIK BACKMAN');
    expect(title?.creator).toBeUndefined();
  });

  it('does not apply when the biggest text is not a name', () => {
    const title = coverTitleOf([line('THE GREAT GATSBY', 0.1, 0.3, 0.8, 0.2), line('Francis Scott', 0.2, 0.8, 0.5, 0.1)], PAGE, T);
    expect(title?.line.text).toBe('THE GREAT GATSBY');
    expect(title?.creator).toBeUndefined();
  });

  it('ignores a blurb or a bestseller line that is bigger than half the name', () => {
    const lines = [...author, line('#1 New York Times bestseller', 0.1, 0.6, 0.8, 0.1), line('A NOVEL', 0.4, 0.8, 0.2, 0.09)];
    const title = coverTitleOf(lines, PAGE, T);
    expect(title?.line.text).toBe('FREDRIK BACKMAN');
    expect(title?.creator).toBeUndefined();
  });

  it('keeps the author and the title apart where a line a cover prints beside the title stands between them', () => {
    // "A NOVEL" fills the gap between BACKMAN and MY FRIENDS, so the two big runs are not one title.
    const lines = [...author, line('A NOVEL', 0.46, 0.41, 0.08, 0.025), line('MY FRIENDS', 0.23, 0.44, 0.53, 0.11)];
    const title = coverTitleOf(lines, PAGE, T);
    expect(title?.line.text).toBe('MY FRIENDS');
    expect(title?.creator?.text).toBe('FREDRIK BACKMAN');
  });

  it('leaves two lines joined when the boilerplate line stands beside the gap, not in it (a logo at the edge splits nothing)', () => {
    const lines = [line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1), line('DVD', 0.86, 0.76, 0.1, 0.03), line('CAMP 3', 0.2, 0.8, 0.38, 0.11)];
    expect(titleOf(lines)).toBe('SLEEP AWAY CAMP 3');
  });
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
    ['dvd-01', 'SLEEP AWAY CAMP 3 TÉỂNAGÉ WASTELAND'], // its subtitle (0.65x and 0.59x) joins: the search for "SLEEP AWAY CAMP 3" alone found another film
    ['dvd-02', 'CONGO'],
    ['game-box-01', 'WING CONNANDER'],
    ['paperback-01', 'MY FRIENDS'], // FREDRIK BACKMAN is printed bigger than the title: that is the creator
    ['paperback-03', 'Theo of Golden'],
    ['paperback-04', 'Strangers'],
    ['vhs-02', 'PINOCCHIO'],
  ])('reads %s as %s', async (id, title) => {
    const reading = await readCover(id);
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title).toBe(title);
  });

  // The lines cannot give these whole: a garbled cartridge label; a title Vision read as "Stórýßoök" over "Ending" (set at 0.55x of it, which
  // would also let the "5" over RUSHMORE in at 0.54x), so "Storybook Ending" cannot be made honestly; and "RED" over "OCTOBER", where the
  // actor BALDWIN under it is set at 0.69x, bigger than OCTOBER's 0.66x, so no height separates them. The engine must still not take a blurb.
  const BLURB = /best.?sell|new york times|novel|licensed|assembled|made in|nintendo|playstation|npr|charming|monaghan|dolby|criterion|author of|siegel|breathtaking/i;

  it.each(Object.keys(covers))('does not take a blurb for the title of %s', async (id) => {
    const reading = await readCover(id);
    expect(reading.items[0].title).not.toMatch(BLURB);
  });

  it.each([
    ['paperback-02', 'Stórýßoök'],
    ['vhs-01', 'RED OCTOBER BALDVIN'],
  ])('gives %s the part of its title the lines hold: %s', async (id, title) => {
    const reading = await readCover(id);
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title).toBe(title);
  });

  it('gives vhs-01 a title with both RED and OCTOBER in it', async () => {
    expect((await readCover('vhs-01')).items[0].title).toMatch(/RED.*OCTOBER/);
  });

  it.each(['game-cartridge-01', 'game-cartridge-02'])('still gives %s one item with some title', async (id) => {
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

describe('the creator the engine sends for the twelve real covers', () => {
  // A cover's creator is only the name the name rule found: the leftover lines gave "Tatiplay", "DANKE", "praun l", "TNE", "a Menoir", the cast of
  // CONGO. The server compares a read's creator with each candidate's, so a wrong one costs a right title score and a search rung.
  it.each(Object.keys(covers).filter((id) => id !== 'paperback-01'))('sends no creator for %s', async (id) => {
    expect((await readCover(id)).items[0].creator).toBeUndefined();
  });

  it('sends FREDRIK BACKMAN for paperback-01, whose author is printed bigger than the title', async () => {
    expect((await readCover('paperback-01')).items[0].creator).toBe('FREDRIK BACKMAN');
  });
});

describe('the creator of a single cover', () => {
  const readLines = async (lines: TextLine[]) => {
    const still: Still = { uri: 'fixture://creator', ...PAGE };
    const engine = new VisionPipelineEngine({ vision: new RecordingVision({ [still.uri]: { text: lines } }), settings: () => S, newId: testIds });
    return (await engine.read(still, () => {}, { shotKind: 'single_cover' })).items[0];
  };

  it('is the name printed bigger than the title when the title is the smaller run', async () => {
    const item = await readLines([line('FREDRIK', 0.26, 0.1, 0.43, 0.13), line('BACKMAN', 0.19, 0.25, 0.6, 0.15), line('MY FRIENDS', 0.23, 0.7, 0.53, 0.11)]);
    expect(item.title).toBe('MY FRIENDS');
    expect(item.creator).toBe('FREDRIK BACKMAN');
  });

  it('is nothing when a name-like author line is smaller than the title, so the title is not swapped', async () => {
    const item = await readLines([line('JAWS', 0.3, 0.3, 0.4, 0.2), line('Peter Benchley', 0.3, 0.8, 0.4, 0.05)]);
    expect(item.title).toBe('JAWS');
    expect(item.creator).toBeUndefined();
  });

  it('is nothing for a title of several lines with a name-like line beside it', async () => {
    const title = [line('SLEEP AWAY', 0.15, 0.65, 0.63, 0.1), line('CAMP 3', 0.2, 0.75, 0.38, 0.11), line('CAMP B', 0.2, 0.745, 0.39, 0.115, 0.5)];
    const item = await readLines([...title, line('Jane Roe', 0.3, 0.9, 0.3, 0.04)]);
    expect(item.title).toBe('SLEEP AWAY CAMP 3');
    expect(item.creator).toBeUndefined();
  });

  it('is nothing when no line can be the title (a bestseller line, a slanted name), so the title falls back to the longest line', async () => {
    const item = await readLines([line('#1 New York Times bestseller', 0.1, 0.1, 0.8, 0.1), line('Peter Benchley', 0.05, 0, 0.2, 0.3)]);
    expect(item.title).toBe('#1 New York Times bestseller');
    expect(item.creator).toBeUndefined();
  });
});

describe('a shelf shot still picks each piece title as it did: the most confident of the longest lines', () => {
  // Two spines. On the first the short line is the bigger; on the second the longer line is the bigger but the unsure one. The shelf rule
  // (confidence times length) takes the long tagline on the first and the short sure line on the second: size is not what it goes by.
  const spines: Scene = {
    text: [
      line('A very long tagline set in tiny type', 0.05, 0.1, 0.4, 0.03),
      line('BIG', 0.1, 0.4, 0.2, 0.08),
      line('Longer and unsure', 0.53, 0.2, 0.44, 0.14, 0.35),
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

  it('and still offers a name-like line as the creator, where a single cover sends none', async () => {
    const lines = [line('THE GREAT GATSBY', 0.1, 0.3, 0.8, 0.2), line('NATIONAL', 0.05, 0.05, 0.2, 0.04), line('Francis Scott', 0.1, 0.8, 0.5, 0.05)];
    const shelf = new VisionPipelineEngine({ vision: new RecordingVision({ [spineStill.uri]: { text: lines } }), settings: () => S, newId: testIds });
    expect((await shelf.read(spineStill, () => {}, { shotKind: 'shelf_segment' })).items[0].creator).toBe('NATIONAL');
    expect((await shelf.read(spineStill, () => {}, { shotKind: 'single_cover' })).items[0].creator).toBeUndefined();
  });

  it('while the same lines read as one cover take the biggest letters', async () => {
    const reading = await readSpines({ shotKind: 'single_cover' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].title).toBe('Longer and unsure');
  });
});
