// The whole cue a single cover gives (its case's format and what its text says it is) and how that cue corrects what an engine read.
import type { Rect } from '@/native/cm-vision';
import { type CoverCue, correctRead, coverCue, describeCue } from './coverCue';
import type { CoverFormatInput, FilmFormat } from './coverFormat';
import { line } from './fakes';
import type { ReadItem } from './types';

const STILL = { width: 3000, height: 4000 };
const said = (...texts: string[]) => texts.map((t, i) => line(t, 0.1, 0.1 + i * 0.05, 0.5, 0.04));
/** A case standing upright, `ratio` wide for its height, `fill` of the picture tall (80% unless said). */
const caseWith = (ratio: number, fill = 0.8): Rect => {
  const width = (ratio * fill * STILL.height) / STILL.width;
  return { x: (1 - width) / 2, y: (1 - fill) / 2, width, height: fill };
};

describe('coverCue', () => {
  it('has the format the case says and the kind the text says', () => {
    expect(coverCue({ still: STILL, rectangles: [caseWith(0.86)], lines: said('THE CRITERION COLLECTION') })).toEqual({
      format: { format: 'bluray', source: 'shape' },
      kind: 'film',
    });
  });

  it('has just a format when the text names no kind', () => {
    expect(coverCue({ still: STILL, rectangles: [], lines: said('Blu-ray Disc') })).toEqual({ format: { format: 'bluray', source: 'logo' } });
  });

  it('has just a kind when the case says no format', () => {
    expect(coverCue({ still: STILL, rectangles: [], lines: said('A NOVEL') })).toEqual({ kind: 'book' });
  });

  it('is nothing when neither says anything', () => {
    expect(coverCue({ still: STILL, rectangles: [], lines: said('THE LANTERN ROAD') })).toBeUndefined();
  });

  it('has the product code the text prints', () => {
    expect(coverCue({ still: STILL, rectangles: [], lines: said('SNS-8C-USA') })).toEqual({ productCode: 'SNS-8C-USA' });
  });

  it('has the kind and the code together on a cartridge', () => {
    expect(coverCue({ still: STILL, rectangles: [], lines: said('SUPER NINTENDO', 'SNS ADNE-USA') })).toEqual({
      kind: 'game',
      productCode: 'SNS-ADNE-USA',
    });
  });
});

describe('describeCue: what the eval record says the cue saw', () => {
  const FRAME: Rect = { x: 0, y: 0, width: 1, height: 1 };
  const input = (rectangles: Rect[], ...texts: string[]): CoverFormatInput => ({ still: STILL, rectangles, lines: said(...texts) });
  const described = (look: CoverFormatInput, arrival: Parameters<typeof describeCue>[0] = 'ready') => describeCue(arrival, { input: look, cue: coverCue(look) });

  it('says a Blu-ray-shaped rectangle gave the shape format, with the ratio and how much of the picture it covered', () => {
    expect(described(input([caseWith(0.86)]))).toBe('cue=ready fmt=shape:bluray r=0.86 cov=0.73 rects=1 kind=none code=none');
  });

  it('says a logo gave the format, and still gives the ratio of the rectangle', () => {
    expect(described(input([caseWith(0.71)], 'Blu-ray Disc'))).toBe('cue=ready fmt=logo:bluray r=0.71 cov=0.61 rects=1 kind=none code=none');
  });

  it('says there were no rectangles', () => {
    expect(described(input([]))).toBe('cue=ready fmt=none rects=0 kind=none code=none');
  });

  it('says a rectangle that hugs the frame was left out', () => {
    expect(described(input([FRAME]))).toBe('cue=ready fmt=none rects=1 (1 frame-hugging) kind=none code=none');
  });

  it('says a rectangle too small to be the case was left out, and how much it covered', () => {
    expect(described(input([caseWith(0.86, 0.4)]))).toBe('cue=ready fmt=none r=0.86 cov=0.18 rects=1 (1 too small) kind=none code=none');
  });

  it('says a rectangle shaped like no case was left out', () => {
    expect(described(input([caseWith(0.787)]))).toBe('cue=ready fmt=none r=0.79 cov=0.67 rects=1 (1 out of band) kind=none code=none');
  });

  it('counts each reason once per rectangle', () => {
    const look = input([FRAME, caseWith(0.86, 0.4), { x: 0.1, y: 0.1, width: 0, height: 0.5 }, caseWith(0.55)]);
    expect(described(look)).toContain('rects=4 (1 unusable, 1 frame-hugging, 1 too small)');
  });

  it('says the kind the text named and the product code it printed', () => {
    expect(described(input([], 'SUPER NINTENDO', 'SNS ADNE-USA'))).toBe('cue=ready fmt=none rects=0 kind=game code=SNS-ADNE-USA');
  });

  it.each(['ready', 'late'] as const)('says the cue was %s when it arrived', (arrival) => {
    expect(described(input([]), arrival)).toMatch(new RegExp(`^cue=${arrival} `));
  });

  it.each(['timeout', 'failed'] as const)('says only that the cue %s when there is nothing to describe', (arrival) => {
    expect(describeCue(arrival)).toBe(`cue=${arrival}`);
  });

  it('stays short, whatever the cover had on it', () => {
    const many = Array.from({ length: 40 }, () => FRAME);
    const worst = described(input([...many, ...Array.from({ length: 40 }, () => caseWith(0.86, 0.4))], 'Ultra HD', 'SUPER NINTENDO', 'SNS ADNE-USA'), 'late');
    expect(worst.length).toBeLessThan(200);
  });
});

describe('correctRead', () => {
  const read = (kind: ReadItem['kind'], format?: ReadItem['format'], extra: Partial<ReadItem> = {}): ReadItem => ({
    localId: 'id',
    text: 'RUSHMORE',
    title: 'Rushmore',
    kind,
    ...(format ? { format } : {}),
    order: 0,
    confidence: 'high',
    ...extra,
  });
  const as = (item: ReadItem) => `${item.kind}/${item.format}`;
  const logo = (format: FilmFormat, kind?: CoverCue['kind']): CoverCue => ({ format: { format, source: 'logo' }, ...(kind ? { kind } : {}) });
  const shape = (format: FilmFormat, kind?: CoverCue['kind']): CoverCue => ({ format: { format, source: 'shape' }, ...(kind ? { kind } : {}) });

  describe('a format logo', () => {
    it('makes a book or an "other" a film in that format, and switches a film format', () => {
      expect(as(correctRead(read('book'), logo('bluray')))).toBe('film/bluray');
      expect(as(correctRead(read('other'), logo('dvd')))).toBe('film/dvd');
      expect(as(correctRead(read('film', 'dvd'), logo('uhdBluray')))).toBe('film/uhd_bluray');
    });

    it('beats every kind cue', () => {
      expect(as(correctRead(read('other'), logo('dvd', 'game')))).toBe('film/dvd');
      expect(as(correctRead(read('other'), logo('dvd', 'book')))).toBe('film/dvd');
    });

    it('never changes a game', () => {
      const game = read('game');
      expect(correctRead(game, logo('bluray'))).toBe(game);
    });
  });

  describe('the Criterion Collection', () => {
    const criterion: CoverCue = { kind: 'film' };

    it('makes a book or an "other" a film, with no format when nothing says one', () => {
      expect(as(correctRead(read('book'), criterion))).toBe('film/undefined');
      expect(as(correctRead(read('other'), criterion))).toBe('film/undefined');
    });

    it('leaves a film as the engine read it', () => {
      const film = read('film', 'dvd');
      expect(correctRead(film, criterion)).toBe(film);
    });

    it('takes the format from the shape of the case', () => {
      expect(as(correctRead(read('book'), { ...criterion, ...shape('bluray') }))).toBe('film/bluray');
      expect(as(correctRead(read('other'), { ...criterion, ...shape('bluray') }))).toBe('film/bluray');
      expect(as(correctRead(read('film', 'dvd'), { ...criterion, ...shape('bluray') }))).toBe('film/bluray');
    });

    it('never changes a game', () => {
      const game = read('game');
      expect(correctRead(game, criterion)).toBe(game);
    });

    it('keeps what else the engine read', () => {
      const corrected = correctRead(read('book', undefined, { creator: 'Wes Anderson', edition: 'Criterion Collection' }), criterion);
      expect(corrected).toMatchObject({ title: 'Rushmore', creator: 'Wes Anderson', edition: 'Criterion Collection', confidence: 'high', localId: 'id' });
    });
  });

  describe('a console\'s name', () => {
    const consoleName: CoverCue = { kind: 'game' };

    it('makes a book, a film or an "other" a game with no film format', () => {
      expect(as(correctRead(read('book'), consoleName))).toBe('game/undefined');
      expect(as(correctRead(read('other'), consoleName))).toBe('game/undefined');
      expect(as(correctRead(read('film', 'dvd'), consoleName))).toBe('game/undefined');
    });

    it('keeps the platform and the completeness the engine read', () => {
      const corrected = correctRead(read('other', undefined, { platform: 'Super Nintendo', completeness: 'cart_only' }), consoleName);
      expect(corrected).toMatchObject({ kind: 'game', platform: 'Super Nintendo', completeness: 'cart_only' });
    });

    it('leaves a game as the engine read it, whatever the shape', () => {
      const game = read('game');
      expect(correctRead(game, consoleName)).toBe(game);
      expect(correctRead(game, { ...consoleName, ...shape('vhs') })).toBe(game);
    });
  });

  describe('a bestseller line', () => {
    const bestseller: CoverCue = { kind: 'book' };

    it('makes an "other" a book', () => {
      expect(as(correctRead(read('other'), bestseller))).toBe('book/undefined');
    });

    it('leaves every read that already has a kind', () => {
      for (const item of [read('book'), read('film', 'dvd'), read('film', 'vhs'), read('game')]) {
        expect(correctRead(item, bestseller)).toBe(item);
      }
    });
  });

  describe('the shape of the case alone', () => {
    it('switches one film format for another', () => {
      expect(as(correctRead(read('film', 'dvd'), shape('vhs')))).toBe('film/vhs');
      expect(as(correctRead(read('film', 'dvd'), shape('bluray')))).toBe('film/bluray');
    });

    it('never makes a book, a game or an "other" into a film', () => {
      for (const item of [read('book'), read('game'), read('other')]) {
        expect(correctRead(item, shape('dvd'))).toBe(item);
      }
    });

    it('does not make an "other" a film when a bestseller line made it a book', () => {
      expect(as(correctRead(read('other'), shape('vhs', 'book')))).toBe('book/undefined');
    });
  });

  describe('a product code', () => {
    const code: CoverCue = { productCode: 'SNS-8C-USA' };

    it('makes an "other" a game that carries the code, with no film format', () => {
      expect(correctRead(read('other'), code)).toMatchObject({ kind: 'game', productCode: 'SNS-8C-USA' });
      expect(correctRead(read('other'), code).format).toBeUndefined();
    });

    it('puts the code on a game without changing anything else about it', () => {
      const corrected = correctRead(read('game', undefined, { platform: 'Super Nintendo', completeness: 'cart_only' }), code);
      expect(corrected).toMatchObject({ kind: 'game', platform: 'Super Nintendo', completeness: 'cart_only', productCode: 'SNS-8C-USA', title: 'Rushmore' });
    });

    it('never changes a book or a film because of a code-like string, and does not give it the code', () => {
      for (const item of [read('book'), read('film', 'dvd'), read('film', 'vhs'), read('tv')]) {
        expect(correctRead(item, code)).toBe(item);
      }
    });

    it('is not carried to a film that a format logo made out of an "other"', () => {
      const corrected = correctRead(read('other'), { ...code, ...logo('dvd') });
      expect(as(corrected)).toBe('film/dvd');
      expect(corrected.productCode).toBeUndefined();
    });

    it('goes with the game that a console\'s name made of a book read', () => {
      expect(correctRead(read('book'), { ...code, kind: 'game' })).toMatchObject({ kind: 'game', productCode: 'SNS-8C-USA' });
    });

    it('is not carried to a book that a bestseller line made of an "other"', () => {
      const corrected = correctRead(read('other'), { ...code, kind: 'book' });
      expect(as(corrected)).toBe('book/undefined');
      expect(corrected.productCode).toBeUndefined();
    });

    it('returns the same object when the game already has the code', () => {
      const game = read('game', undefined, { productCode: 'SNS-8C-USA' });
      expect(correctRead(game, code)).toBe(game);
    });

    it('does not change the item it is given', () => {
      const other = read('other');
      correctRead(other, code);
      expect(other).toEqual(read('other'));
    });
  });

  it('returns the same object when the cue changes nothing', () => {
    const film = read('film', 'dvd');
    expect(correctRead(film, logo('dvd'))).toBe(film);
    expect(correctRead(film, {})).toBe(film);
  });

  it('does not change the item it is given', () => {
    const book = read('book');
    correctRead(book, { kind: 'film', ...shape('bluray') });
    expect(book).toEqual(read('book'));
  });
});
