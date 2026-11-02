import { printedWords, printSupports, titleWords } from './printSupport';

const printOf = (...lines: string[]) => printedWords(lines.map((text) => ({ text })));

describe('titleWords', () => {
  it('lowercases the title, splits it on anything that is not a letter or a digit, and drops stop words and one-letter words', () => {
    expect(titleWords('The Hunt for RED October!')).toEqual(['hunt', 'red', 'october']);
    expect(titleWords("Ender's Game: Part 2 - a Sequel")).toEqual(['ender', 'game', 'part', 'sequel']);
  });

  it('drops every stop word', () => {
    expect(titleWords('the a an of and to in on for at by with from or is Wonder')).toEqual(['wonder']);
  });

  it('keeps a number of two digits or more and drops a lone digit', () => {
    expect(titleWords('Fahrenheit 451 and 9')).toEqual(['fahrenheit', '451']);
  });
});

describe('printedWords', () => {
  it('is every word of every line, lowercased, whatever the line is', () => {
    const printed = printOf('KIMBA THE LION', 'Walt-Disney 1994', '  ');
    expect([...printed].sort()).toEqual(['1994', 'disney', 'kimba', 'lion', 'the', 'walt']);
  });

  it('is empty when there are no lines', () => {
    expect(printedWords([]).size).toBe(0);
  });
});

describe('printSupports: a model read added on its own needs the shelf\'s print to have its words', () => {
  it('does not support a title none of whose words the shelf printed', () => {
    expect(printSupports('The Matrix', printOf('DUNE', 'FRANK HERBERT', 'EMMA'))).toBe(false);
  });

  it('supports a title with two thirds of its words printed, stop words not counted', () => {
    expect(printSupports('THE FIRST FOR A RED OCTOBER', printOf('RED', 'OCTOBER'))).toBe(true);
  });

  it('does not support a title with less than two thirds of its words printed', () => {
    expect(printSupports('The Lion, the Witch and the Wardrobe', printOf('KIMBA THE LION'))).toBe(false);
  });

  it('supports a title with every word printed, on any line', () => {
    expect(printSupports('Neuromancer', printOf('some', 'NEUROMANCER'))).toBe(true);
    expect(printSupports('Lantern Road Cookbook', printOf('LANTERN ROAD', 'COOKBOOK'))).toBe(true);
  });

  it('counts a plural as the word printed, either way round', () => {
    expect(printSupports('Queen of Shadow', printOf('SHADOWS', 'QUEEN'))).toBe(true);
    expect(printSupports('Queen of Shadows', printOf('SHADOW', 'QUEEN'))).toBe(true);
    expect(printSupports('Shadows', printOf('SHADOWING'))).toBe(false); // only a trailing s, nothing longer
  });

  it('counts a word one edit away as printed: a letter wrong, missing or extra', () => {
    expect(printSupports('Alchemised', printOf('ALCHEMISEO'))).toBe(true); // substituted
    expect(printSupports('Alchemised', printOf('ALCHEMISD'))).toBe(true); // deleted
    expect(printSupports('Alchemised', printOf('ALCHEMISSED'))).toBe(true); // inserted
    expect(printSupports('Alchemised', printOf('ALCHEMISXO'))).toBe(false); // two edits
  });

  it('does not count two letters swapped as one edit', () => {
    expect(printSupports('Alchemised', printOf('ALCHEMISDE'))).toBe(false);
  });

  it('does not read short words fuzzily: both words must be at least five letters for an edit to count', () => {
    expect(printSupports('Cat', printOf('CAR'))).toBe(false);
    expect(printSupports('Dune', printOf('DUNK'))).toBe(false);
    expect(printSupports('Emmas', printOf('EMMAS'))).toBe(true); // equal is always printed
    expect(printSupports('Heart', printOf('HEARD'))).toBe(true); // five letters each
    expect(printSupports('Heart', printOf('HEAR'))).toBe(false); // the print word is only four
  });

  it('does not support a title made only of stop words, or with no words at all', () => {
    expect(printSupports('The Of A', printOf('THE', 'OF', 'A'))).toBe(false);
    expect(printSupports('', printOf('THE'))).toBe(false);
    expect(printSupports('!!', printOf('THE'))).toBe(false);
  });

  it('does not support anything when nothing is printed', () => {
    expect(printSupports('Dune', printOf())).toBe(false);
  });
});
