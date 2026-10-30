// A game's product code, read from Vision's text lines (ADR-031). Strict patterns, whole tokens only: a wrong code costs a wrong title,
// and a missed one costs nothing the server cannot find another way.
import coverReads from './coverReads.json';
import { productCodeOf } from './productCode';

const said = (...texts: string[]) => texts.map((text) => ({ text }));
const codeIn = (...texts: string[]) => productCodeOf(said(...texts));

const recorded = coverReads as Record<string, { lines: { text: string }[] }>;

describe('productCodeOf on real covers', () => {
  it('reads SNS-8C-USA from the first SNES cartridge', () => {
    expect(productCodeOf(recorded['game-cartridge-01'].lines)).toBe('SNS-8C-USA');
  });

  it('reads SNS ADNE-USA from the second SNES cartridge, spelled with a hyphen', () => {
    expect(productCodeOf(recorded['game-cartridge-02'].lines)).toBe('SNS-ADNE-USA');
  });

  it.each(Object.keys(recorded).filter((id) => !id.startsWith('game-cartridge')))('finds no code on %s', (id) => {
    expect(productCodeOf(recorded[id].lines)).toBeUndefined();
  });
});

describe('productCodeOf: cartridge codes', () => {
  it.each([
    ['SNS-8C-USA', 'SNS-8C-USA'],
    ['SNS ADNE-USA', 'SNS-ADNE-USA'],
    ['SNS-ADNE-USA', 'SNS-ADNE-USA'],
    ['NUS-NSME-USA', 'NUS-NSME-USA'],
    ['NUS NSME USA', 'NUS-NSME-USA'],
    ['DMG-AB-USA', 'DMG-AB-USA'],
    ['AGB-AXVE-USA', 'AGB-AXVE-USA'],
    ['DOL-GALE-USA', 'DOL-GALE-USA'],
    ['RVL-RSPE-USA', 'RVL-RSPE-USA'],
    ['NTR-AMCE-USA', 'NTR-AMCE-USA'],
    ['TWL-VKGE-USA', 'TWL-VKGE-USA'],
    ['SHVC-YI', 'SHVC-YI'],
    ['HVC-SM', 'HVC-SM'],
    ['SNS8C', 'SNS-8C'],
    ['SNS 8C', 'SNS-8C'],
    ['SNSADNEUSA', 'SNS-ADNE-USA'],
  ])('reads "%s" as %s', (text, code) => {
    expect(codeIn(text)).toBe(code);
  });

  it('reads a handheld or console code with a one-letter group before its id', () => {
    expect(codeIn('CTR-P-AXXE')).toBe('CTR-P-AXXE');
    expect(codeIn('HAC-P-AABCA')).toBe('HAC-P-AABCA');
    expect(codeIn('KTR-P-AXXE')).toBe('KTR-P-AXXE');
    expect(codeIn('BEE-P-AAAAA')).toBe('BEE-P-AAAAA');
  });

  it.each(['SNS', 'SHVC', 'NUS', 'NES', 'HVC', 'DMG', 'CGB', 'AGB', 'NTR', 'TWL', 'CTR', 'KTR', 'DOL', 'RVL', 'HAC', 'BEE'])(
    'knows the prefix %s',
    (prefix) => {
      expect(codeIn(`${prefix}-AB1E-USA`)).toBe(`${prefix}-AB1E-USA`);
      expect(codeIn(`${prefix} AB1E`)).toBe(`${prefix}-AB1E`);
    },
  );

  it.each(['USA', 'EUR', 'JPN', 'NOE', 'UKV', 'AUS', 'FRA', 'ESP', 'ITA', 'CAN'])('knows the region %s, after a hyphen or a space', (region) => {
    expect(codeIn(`SNS-AB1E-${region}`)).toBe(`SNS-AB1E-${region}`);
    expect(codeIn(`SNS-AB1E ${region}`)).toBe(`SNS-AB1E-${region}`);
  });

  it('finds the code among other words on the line', () => {
    expect(codeIn('ASSEMBLED IN MEXICO SNS-8C-USA')).toBe('SNS-8C-USA');
    expect(codeIn('(SNS-8C-USA)')).toBe('SNS-8C-USA');
    expect(codeIn('SNS-8C-USA.')).toBe('SNS-8C-USA');
  });

  it('finds the code on any line', () => {
    expect(codeIn('MADE IN JAPAN', 'Nintendo', 'SNS-8C-USA')).toBe('SNS-8C-USA');
  });

  it('leaves off a trailing group that is not a region it knows', () => {
    expect(codeIn('SNS-8C-XYZ')).toBe('SNS-8C');
  });

  it.each([
    ['letters after a space with nothing to say it is a code', 'NES GAME'],
    ['a year after a space', 'NES 1985'],
    ['a plain word that starts with a prefix', 'NESTLE'],
    ['a plain word that starts with a prefix, longer', 'DOLLAR'],
    ['a plain word that starts with a prefix, shorter', 'HACK'],
    ['a plain word that starts with a prefix, third', 'BEEF'],
    ['a region where the id should be', 'SNS-USA'],
    ['the prefix with a hyphen and nothing after it', 'SNS-'],
    ['a prefix in the middle of a word', 'XSNS-8C-USA'],
    ['an id with a letter stuck on its end', 'SNS-8CX1234-USA'],
    ['lower case, which a label never prints', 'sns-8c-usa'],
  ])('is not a code: %s', (_why, text) => {
    expect(codeIn(text)).toBeUndefined();
  });
});

describe('productCodeOf: disc codes', () => {
  const PREFIXES = [
    'SLUS', 'SCUS', 'SLES', 'SCES', 'SLPS', 'SCPS', 'SLPM', 'SLKA', 'SCKA',
    'BLUS', 'BCUS', 'BLES', 'BCES', 'BLJM', 'ULUS', 'UCUS', 'ULES', 'UCES',
  ];

  it.each(PREFIXES)('knows the prefix %s, however its digits are written', (prefix) => {
    for (const written of [`${prefix}-00594`, `${prefix} 00594`, `${prefix}_005.94`, `${prefix}00594`]) {
      expect(codeIn(written)).toBe(`${prefix}-00594`);
    }
  });

  it.each([
    ['SLUS-00594', 'SLUS-00594'],
    ['SLUS 00594', 'SLUS-00594'],
    ['SLUS_005.94', 'SLUS-00594'],
    ['SLUS00594', 'SLUS-00594'],
    ['SLES-50612', 'SLES-50612'],
    ['SCUS-94163', 'SCUS-94163'],
    ['Disc SLUS-00594 Made in USA', 'SLUS-00594'],
  ])('reads "%s" as %s', (text, code) => {
    expect(codeIn(text)).toBe(code);
  });

  it.each([
    ['too few digits', 'SLUS-0059'],
    ['too many digits', 'SLUS-005941'],
    ['a letter after the digits', 'SLUS-00594X'],
    ['a prefix that is not on the list', 'SLXX-00594'],
    ['a prefix inside a longer word', 'XSLUS-00594'],
    ['no digits', 'SLUS'],
  ])('is not a code: %s', (_why, text) => {
    expect(codeIn(text)).toBeUndefined();
  });
});

describe('productCodeOf: Sega codes', () => {
  it.each([
    ['MK-1079', 'MK-1079'],
    ['MK-12345', 'MK-12345'],
    ['MK-1079-50', 'MK-1079'],
  ])('reads "%s" as %s', (text, code) => {
    expect(codeIn(text)).toBe(code);
  });

  it.each([
    ['three digits', 'MK-123'],
    ['six digits', 'MK-123456'],
    ['no hyphen', 'MK1079'],
    ['T- codes, which are too ambiguous', 'T-12345'],
    ['a letter after the digits', 'MK-1079X'],
  ])('is not a code: %s', (_why, text) => {
    expect(codeIn(text)).toBeUndefined();
  });
});

describe('productCodeOf: things that are never codes', () => {
  it.each([
    ['an ISBN-13', 'ISBN 978-0-306-40615-7'],
    ['an ISBN-13 with its label', 'ISBN-13: 978-3-16-148410-0'],
    ['an ISBN-10', 'ISBN 0-306-40615-2'],
    ['a bare ISBN', '9780306406157'],
    ['where it was made', 'MADE IN JAPAN'],
    ['a rating', 'PG-13'],
    ['a film format', 'DVD-VIDEO'],
    ['a console prefix on its own', 'SNS'],
    ['a bare number', '1234567'],
    ['a bare short number', '00594'],
    ['a price', '$19.99'],
    ['a title', 'THE LANTERN ROAD'],
    ['nothing at all', ''],
  ])('is not a code: %s', (_why, text) => {
    expect(codeIn(text)).toBeUndefined();
  });

  it('finds nothing on no lines', () => {
    expect(productCodeOf([])).toBeUndefined();
  });
});

describe('productCodeOf: more than one code', () => {
  it('reads the same code printed twice as that code', () => {
    expect(codeIn('SNS-8C-USA', 'SNS 8C USA')).toBe('SNS-8C-USA');
  });

  it('reads the code with its region when the same code is also printed without one', () => {
    expect(codeIn('SNS-8C', 'SNS-8C-USA')).toBe('SNS-8C-USA');
  });

  it('says nothing when two different codes are on the picture: either could be a misread', () => {
    expect(codeIn('SNS-8C-USA', 'SNS-8B-USA')).toBeUndefined();
    expect(codeIn('SNS-8C-USA', 'SLUS-00594')).toBeUndefined();
  });
});

describe('productCodeOf: what the server takes', () => {
  it('never returns a code longer than the 32 characters the request allows', () => {
    const longest = ['CTR-P-AXXE-USA', 'HAC-P-AABCA-USA', 'SLUS_005.94', 'BEE-P-AAAAA-UKV', 'SHVC-ABCDE-JPN'].map((text) => codeIn(text));
    for (const code of longest) {
      expect(code).toBeDefined();
      expect(code?.length).toBeLessThanOrEqual(32);
    }
  });

  it('spells every separator with a hyphen and every letter in upper case', () => {
    expect(codeIn('SLUS_005.94')).toMatch(/^[A-Z0-9-]+$/);
    expect(codeIn('SNS ADNE-USA')).toMatch(/^[A-Z0-9-]+$/);
  });
});
