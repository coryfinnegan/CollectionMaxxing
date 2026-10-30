// What the printed text of a single cover says about the kind of item it is: a film (Criterion), a game (a console's name), a book
// (a bestseller line). Only whole lines count for a book, and cues that disagree say nothing.
import type { TextLine } from '@/native/cm-vision';
import coverReads from './coverReads.json';
import { coverKind } from './coverKind';
import { line } from './fakes';

/** Lines of text anywhere on the picture; only their words matter to a kind cue. */
const said = (...texts: string[]): TextLine[] => texts.map((t, i) => line(t, 0.1, 0.1 + i * 0.05, 0.5, 0.04));
const kindOf = (...texts: string[]) => coverKind(said(...texts));

interface RecordedCover {
  lines: { text: string; x: number; y: number; width: number; height: number }[];
}
const recorded = coverReads as Record<string, RecordedCover>;
const fromRecording = (id: string): TextLine[] => recorded[id].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));

describe('coverKind: the Criterion Collection is a film', () => {
  it.each([
    ['THE CRITERION COLLECTION'],
    ['The Criterion Collection'],
    ['Criterion Collection'],
    ['THE CRITERION CO'],
    ['THE CRITERION'],
    ['THE CRITERI0N COLLECTION'],
    ['THE CRITERION COLLECTION™'],
  ])('reads "%s" as a film', (text) => {
    expect(kindOf(text)).toBe('film');
  });

  it('reads the name when the logo is set sideways and split across lines', () => {
    expect(kindOf('THE CRITERION', 'LECTION')).toBe('film');
    expect(kindOf('CRITERION', 'COLLECTION')).toBe('film');
  });

  it.each([
    ['the word on its own, which could be a title', 'CRITERION'],
    ['a sentence that uses the word', 'A criterion for success'],
    ['a collection that is not Criterion', 'The Complete Collection'],
    ['nothing at all', ''],
  ])('finds nothing in %s', (_why, text) => {
    expect(kindOf(text)).toBeUndefined();
  });
});

describe('coverKind: a console\'s name is a game', () => {
  it.each([
    ['PlayStation'],
    ['PLAYSTATION 2'],
    ['E PlayStationlie'],
    ['PlayStationJ|{'],
    ['Nintendo'],
    ['SUPER NINTENDO.'],
    ['LICENSED BY Nintendo'],
    ['Nintendo Switch'],
    ['N64'],
    ['SNES'],
    ['GAME BOY ADVANCE'],
    ['GAMEBOY'],
    ['Xbox 360'],
    ['XBOX360'],
    ['SEGA GENESIS'],
    ['PS2'],
  ])('reads "%s" as a game', (text) => {
    expect(kindOf(text)).toBe('game');
  });

  it.each([
    ['Switch on its own, which is a common title', 'Switch'],
    ['The Switch, which is a title', 'The Switch'],
    ['a name that merely contains a console\'s', 'Segal'],
    ['a console named inside a long blurb', 'The incredible untold story of how Nintendo conquered the world'],
  ])('finds nothing in %s', (_why, text) => {
    expect(kindOf(text)).toBeUndefined();
  });
});

describe('coverKind: a bestseller line is a book', () => {
  it.each([
    ['A NOVEL'],
    ['a Memoir'],
    ['#1 New York Times bestseller'],
    ['#1 NEW YORK TIMES BESTSELLER'],
    ['New York Times Bestseller'],
    ['#1 New York Times best-selling author'],
    ['New York Times bestselling author'],
    ['NATIONAL BESTSELLER'],
    ['INTERNATIONAL BESTSELLER'],
    ['BESTSELLER'],
    ['#I NEW YORK IIMES BESTSELLER'],
    ['HI NEW YORK TIMES BESTSELLER™'],
  ])('reads "%s" as a book', (text) => {
    expect(kindOf(text)).toBe('book');
  });

  it.each([
    // Last eval: CONGO is a film, and says it is made from a novel.
    ['a film adaptation line', 'From the best-selling novel by the author of JURASSIC PARK'],
    ['a film adaptation line without the best-selling', 'From the novel by Michael Crichton'],
    ['"based on the novel"', 'Based on the novel by Stephen King'],
    ['"novel by"', 'A novel by Stephen King'],
    ['an award line that is not a book\'s own', 'bestselling author of'],
    ['a review quote about a bestseller', '"The best New York Times bestseller of the year" - Time'],
    ['a title that merely starts with "A Novel"', 'A Novel Approach to Cooking'],
    ['a title that is only a word of the cue', 'MEMOIR OF A GEISHA'],
    ['the novel alone', 'novel'],
  ])('finds nothing in %s', (_why, text) => {
    expect(kindOf(text)).toBeUndefined();
  });

  it('finds nothing in the film adaptation line, however many other lines the cover has', () => {
    expect(kindOf('CONGO', 'From the best-selling novel by the author of JURASSIC PARK', 'LAURA LINNEY DYLAN WALSH')).toBeUndefined();
  });
});

describe('coverKind: cues that disagree say nothing', () => {
  it.each([
    ['a Criterion film that says it is a novel', ['THE CRITERION COLLECTION', 'A NOVEL']],
    ['a Criterion film beside a console', ['THE CRITERION COLLECTION', 'PlayStation']],
    ['a console beside a bestseller line', ['Nintendo', '#1 New York Times bestseller']],
  ])('%s', (_why, texts) => {
    expect(kindOf(...texts)).toBeUndefined();
  });

  it('is not confused by the same cue seen twice', () => {
    expect(kindOf('A NOVEL', 'a novel')).toBe('book');
    expect(kindOf('Nintendo', 'SUPER NINTENDO')).toBe('game');
  });
});

describe('coverKind on real covers', () => {
  it.each([
    ['bluray-01', 'film'],
    ['game-box-01', 'game'],
    ['game-cartridge-01', 'game'],
    ['game-cartridge-02', 'game'],
    ['paperback-01', 'book'],
    ['paperback-03', 'book'],
    ['paperback-04', 'book'],
  ])('%s is a %s', (id, kind) => {
    expect(coverKind(fromRecording(id))).toBe(kind);
  });

  it.each([
    ['dvd-01', 'THE HUNT FOR RED OCTOBER, a film with no cue of its own'],
    ['dvd-02', 'CONGO, whose "best-selling novel" line is a film adaptation line'],
    ['paperback-02', 'a quote credit that says bestselling author, which is not the book\'s own line'],
    ['vhs-01', 'a VHS slipcase with names on it'],
    ['vhs-02', 'PINOCCHIO'],
  ])('%s has no kind cue (%s)', (id) => {
    expect(coverKind(fromRecording(id))).toBeUndefined();
  });
});
