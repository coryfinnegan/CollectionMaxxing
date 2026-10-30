// A game's product code, read from the text Vision already reads (ADR-031). A cartridge's label art defeats both the text reader and the
// model, but the code printed on it is plain: SNS-8C-USA, NUS-NSME-USA, SLUS-00594. The server looks the code up in its table and names the
// game; this file only finds a code in the lines. Pure: lines in, a code or nothing out.
//
// The patterns are strict, and a code has to be a whole token. A code that is wrong costs a wrong title, and one that is missed costs
// nothing the rest of the read cannot find. So a word that merely starts like a prefix (NESTLE, HACK, BEEF) is not a code, and neither is
// an ISBN, "MADE IN JAPAN", "PG-13", "DVD-VIDEO" or a bare number.
//
// The code comes back in one spelling: upper case, groups joined by hyphens, a disc's digits without their dot. The server normalises
// too, so this only keeps the request tidy.

/** Cartridge systems: the letters before the first hyphen (Super NES, N64, Game Boy, GBA, DS, 3DS, GameCube, Wii, Switch). */
const CARTRIDGE_PREFIXES = ['SNS', 'SHVC', 'NUS', 'NES', 'HVC', 'DMG', 'CGB', 'AGB', 'NTR', 'TWL', 'CTR', 'KTR', 'DOL', 'RVL', 'HAC', 'BEE'];

/** The region a cartridge label may end with. */
const REGIONS = ['USA', 'EUR', 'JPN', 'NOE', 'UKV', 'AUS', 'FRA', 'ESP', 'ITA', 'CAN'];
const REGION_SET: ReadonlySet<string> = new Set(REGIONS);

/** Disc systems (PlayStation 1 to 3, Vita, PSP): five digits follow. */
const DISC_PREFIXES = [
  'SLUS', 'SCUS', 'SLES', 'SCES', 'SLPS', 'SCPS', 'SLPM', 'SLKA', 'SCKA',
  'BLUS', 'BCUS', 'BLES', 'BCES', 'BLJM', 'ULUS', 'UCUS', 'ULES', 'UCES',
];

const alternatives = (words: string[]): string => words.join('|');

/** Not the middle of a longer word: the next character is not a letter or a digit. (The character before the code is checked by `\b`.) */
const END = '(?![A-Za-z0-9])';

// SNS-8C-USA, SNS ADNE-USA, SNS8C, SNSADNEUSA, and CTR-P-AXXE (a one-character group, then the id).
// 1: prefix  2: what separates it from the id  3, 4: a one-character group and the id after it  5: the id  6, 7: the region
const CARTRIDGE = new RegExp(
  `\\b(${alternatives(CARTRIDGE_PREFIXES)})([-\\s]?)(?:([A-Z0-9])-([A-Z0-9]{3,5})|([A-Z0-9]{1,5}))(?:([-\\s]?)(${alternatives(REGIONS)}))?${END}`,
  'g',
);

// SLUS-00594, SLUS 00594, SLUS_005.94, SLUS00594.   1: prefix  2, 3: the digits either side of the dot, if there is one
const DISC = new RegExp(`\\b(${alternatives(DISC_PREFIXES)})[-_\\s]?(\\d{3})\\.?(\\d{2})${END}`, 'g');

// MK-1079. The Sega Genesis and Sega CD list has other letters (T-), but they are too ambiguous to read.
const SEGA = new RegExp(`\\bMK-(\\d{4,5})${END}`, 'g');

/**
 * A cartridge code is only a code when something besides the prefix says so: an explicit hyphen, a region after the id, or an id that
 * mixes letters and digits ("SNS 8C", "SNS8C"). A prefix followed by a plain word or a bare number is not one ("NES GAME", "NES 1985", "NESTLE").
 * An id that is a region ("SNS-USA") is the region, not the id.
 */
function cartridgeCode(match: RegExpExecArray): string | undefined {
  const [, prefix, separator, lead, tail, id, , region] = match;
  const groups = lead === undefined ? [id] : [lead, tail];
  const body = groups.join('');
  const explicit = separator === '-';
  const mixed = /[A-Z]/.test(body) && /\d/.test(body);
  if (lead === undefined && region === undefined && REGION_SET.has(id)) return undefined;
  if (!explicit && region === undefined && !mixed) return undefined;
  return [prefix, ...groups, ...(region === undefined ? [] : [region])].join('-');
}

function scan(text: string, pattern: RegExp, codeOf: (match: RegExpExecArray) => string | undefined): string[] {
  const found: string[] = [];
  pattern.lastIndex = 0;
  for (let match = pattern.exec(text); match; match = pattern.exec(text)) {
    const code = codeOf(match);
    if (code) found.push(code);
    else pattern.lastIndex = match.index + 1; // a refused match must not hide a code that starts inside it
  }
  return found;
}

/** Every code on one line, in the order printed. */
function codesIn(text: string): string[] {
  return [
    ...scan(text, CARTRIDGE, cartridgeCode),
    ...scan(text, DISC, (match) => `${match[1]}-${match[2]}${match[3]}`),
    ...scan(text, SEGA, (match) => `MK-${match[1]}`),
  ];
}

/**
 * The one product code the lines print, or nothing. The same code printed twice is that code, and a code and the same code with its
 * region (SNS-8C, SNS-8C-USA) is the longer one. Two different codes say nothing: one of them is a misread, and the table would name
 * the wrong game.
 */
export function productCodeOf(lines: readonly { text: string }[]): string | undefined {
  const found = [...new Set(lines.flatMap((line) => codesIn(line.text)))];
  const whole = found.filter((code) => !found.some((other) => other.startsWith(`${code}-`)));
  return whole.length === 1 ? whole[0] : undefined;
}
