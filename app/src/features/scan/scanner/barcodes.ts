// Capture-time barcode validation: is this decode plausibly a real barcode, or a partial read?
// Mirrors the server's Identifier rules (docs/02 section 2: UPC-A/E, EAN-8/13, ISBN-10/13, GTIN-14 with
// indicator 0), so a fragment that could never resolve is rejected here with a rescan prompt and never
// queued. A check-valid EAN-8 misdecode still passes: nothing at capture can tell it from a real EAN-8.
const ISBN_LABEL = /^isbn(?:[-\s]?1[03])?\s*:?\s*/i;
const HYPHENS = /[-‐‑‒–—−]/g;

/** The code with the label, hyphens, spaces and any 2 or 5 digit add-on removed, or null when nothing is left. */
function clean(raw: string): string | null {
  if (!raw || !raw.trim()) return null;
  const text = raw.trim().replace(ISBN_LABEL, '').replace(HYPHENS, '');
  const tokens = text.split(/\s+/).filter((t) => t.length > 0);
  const [first, second] = tokens;
  if (first === undefined) return null;
  if (tokens.length === 2 && second !== undefined && /^\d{2}$|^\d{5}$/.test(second) && [8, 10, 12, 13].includes(first.length)) {
    // "03064061 52" is also the ISBN-10 0306406152 in two groups; the ISBN-10 wins over the add-on.
    const whole = first + second;
    return first.length === 8 && whole.length === 10 && isIsbn10(whole) ? whole : first;
  }
  const joined = tokens.join('');
  // A run-on add-on after a 12 or 13 digit code. 8 digit codes are never trimmed (8+2 collides with an
  // ISBN-10, 8+5 with an EAN-13) and neither is 12+2 (a GTIN-14, a different trade item).
  if (/^\d+$/.test(joined)) {
    if (joined.length === 17) return joined.slice(0, 12);
    if (joined.length === 15 || joined.length === 18) return joined.slice(0, 13);
  }
  return joined;
}

/** The GS1 mod-10 check digit for the body (everything but the check digit). */
function gs1Check(body: string): string {
  let sum = 0;
  for (let i = 0; i < body.length; i += 1) {
    const digit = body[body.length - 1 - i];
    sum += Number(digit ?? '') * (i % 2 === 0 ? 3 : 1);
  }
  return String((10 - (sum % 10)) % 10);
}

const isGs1Valid = (digits: string): boolean =>
  digits.length > 1 && gs1Check(digits.slice(0, -1)) === digits[digits.length - 1];

/** Nine digits and a mod-11 check character (`X` for ten), the check in any letter case. */
function isIsbn10(text: string): boolean {
  if (text.length !== 10) return false;
  let sum = 0;
  for (let i = 0; i < 10; i += 1) {
    const c = text[i] ?? '';
    let digit: number;
    if (c >= '0' && c <= '9') digit = Number(c);
    else if (i === 9 && (c === 'X' || c === 'x')) digit = 10;
    else return false;
    sum += digit * (10 - i);
  }
  return sum % 11 === 0;
}

/** Expands an 8 digit UPC-E (number system 0 or 1) to its 12 digit UPC-A, or null when it is not one. */
function expandUpcE(text: string): string | null {
  if (text.length !== 8 || !/^\d+$/.test(text) || (text[0] !== '0' && text[0] !== '1')) return null;
  const ns = text[0];
  const d = text.slice(1, 7);
  const [d0, d1, d2, d3, d4, d5] = [d[0], d[1], d[2], d[3], d[4], d[5]];
  let body: string;
  if (d5 === '0' || d5 === '1' || d5 === '2') body = `${ns}${d0}${d1}${d5}0000${d2}${d3}${d4}`;
  else if (d5 === '3') body = `${ns}${d0}${d1}${d2}00000${d3}${d4}`;
  else if (d5 === '4') body = `${ns}${d0}${d1}${d2}${d3}00000${d4}`;
  else body = `${ns}${d0}${d1}${d2}${d3}${d4}0000${d5}`;
  // The UPC-E check digit is the check digit of the expanded UPC-A.
  return gs1Check(body) === text[7] ? body + text[7] : null;
}

/**
 * True when the decode could be a real barcode the server would resolve: 8, 12, 13 or 14 digits with a
 * valid check digit (UPC-E expands, a GTIN-14 needs indicator 0), or a valid ISBN-10. Anything else is a
 * partial read or not a product code at all.
 */
export function isReadableBarcode(value: string): boolean {
  const text = clean(value);
  if (text === null) return false;
  if (text.length === 10) return isIsbn10(text);
  if (!/^\d+$/.test(text)) return false;
  switch (text.length) {
    case 12:
    case 13:
      return isGs1Valid(text);
    case 14:
      return text[0] === '0' && isGs1Valid(text);
    case 8:
      return expandUpcE(text) !== null || isGs1Valid(text);
    default:
      return false;
  }
}
