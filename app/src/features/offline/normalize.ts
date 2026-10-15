// A small TypeScript port of the server's Gtin / Isbn13 / BarcodeText (CollectionMaxxing.Domain.Identifiers), so that
// the phone and the server agree on what a scanned or typed code is. Keep it in step with those files and their tests.

const HYPHENS = /[-‐‑‒–—−]/g;
const ISBN_LABEL = /^isbn(?:[-\s]?1[03])?\s*:?\s*/i;

const isAllDigits = (text: string) => /^[0-9]+$/.test(text);

/** The GS1 check digit for a body (everything except the last digit). */
function checkDigitFor(body: string): string {
  let sum = 0;
  for (let i = 0; i < body.length; i++) {
    // Counting from the right, the digit next to the check digit weighs 3, then 1, 3, ...
    const weight = (body.length - i) % 2 === 1 ? 3 : 1;
    sum += Number(body[i]) * weight;
  }
  return String((10 - (sum % 10)) % 10);
}

const isValidGs1 = (digits: string) => digits.length >= 2 && checkDigitFor(digits.slice(0, -1)) === digits.slice(-1);

function isValidIsbn10(text: string): boolean {
  if (text.length !== 10) return false;
  let sum = 0;
  for (let i = 0; i < 10; i++) {
    const c = text[i];
    let digit: number;
    if (c >= '0' && c <= '9') digit = Number(c);
    else if (i === 9 && (c === 'X' || c === 'x')) digit = 10;
    else return false;
    sum += digit * (10 - i);
  }
  return sum % 11 === 0;
}

/** Label, hyphens, spaces and 2 or 5 digit add-ons removed; null when nothing is left. Mirrors BarcodeText.Clean. */
function clean(raw: string | null | undefined): string | null {
  if (raw == null || raw.trim() === '') return null;
  const text = raw.trim().replace(ISBN_LABEL, '').replace(HYPHENS, '');
  const tokens = text.split(/\s+/).filter((t) => t !== '');
  if (tokens.length === 0) return null;

  if (
    tokens.length === 2 &&
    (tokens[1].length === 2 || tokens[1].length === 5) &&
    isAllDigits(tokens[1]) &&
    [8, 10, 12, 13].includes(tokens[0].length)
  ) {
    const whole = tokens[0] + tokens[1];
    return tokens[0].length === 8 && whole.length === 10 && isValidIsbn10(whole) ? whole : tokens[0];
  }

  const joined = tokens.join('');
  if (isAllDigits(joined)) {
    if (joined.length === 17) return joined.slice(0, 12);
    if (joined.length === 15 || joined.length === 18) return joined.slice(0, 13);
  }
  return joined;
}

function expandUpcE(text: string): string | null {
  if (text.length !== 8 || !isAllDigits(text) || (text[0] !== '0' && text[0] !== '1')) return null;
  const ns = text[0];
  const d = text.slice(1, 7);
  let body: string;
  switch (d[5]) {
    case '0':
    case '1':
    case '2':
      body = `${ns}${d[0]}${d[1]}${d[5]}0000${d[2]}${d[3]}${d[4]}`;
      break;
    case '3':
      body = `${ns}${d[0]}${d[1]}${d[2]}00000${d[3]}${d[4]}`;
      break;
    case '4':
      body = `${ns}${d[0]}${d[1]}${d[2]}${d[3]}00000${d[4]}`;
      break;
    default:
      body = `${ns}${d[0]}${d[1]}${d[2]}${d[3]}${d[4]}0000${d[5]}`;
  }
  return checkDigitFor(body) === text[7] ? body + text[7] : null;
}

/** The GTIN-13 (or GTIN-8) digits for a barcode, like Gtin.TryParse. */
function parseGtin(text: string): string | null {
  if (!isAllDigits(text)) return null;
  switch (text.length) {
    case 12:
      return isValidGs1(text) ? `0${text}` : null;
    case 13:
      return isValidGs1(text) ? text : null;
    case 14:
      return text[0] === '0' && isValidGs1(text) ? text.slice(1) : null;
    case 8: {
      const expanded = expandUpcE(text);
      if (expanded) return `0${expanded}`;
      return isValidGs1(text) ? text : null;
    }
    default:
      return null;
  }
}

const isBookland = (digits: string) =>
  digits.startsWith('978') || (digits.startsWith('979') && digits.length > 3 && digits[3] !== '0');

/** The ISBN-13 digits for a typed or scanned ISBN-13 or ISBN-10, like Isbn13.TryParse. */
function parseIsbn13(text: string): string | null {
  if (text.length === 10) {
    if (!isValidIsbn10(text)) return null;
    const body = `978${text.slice(0, 9)}`;
    return body + checkDigitFor(body);
  }
  if (text.length !== 13 || !isAllDigits(text) || !isBookland(text) || !isValidGs1(text)) return null;
  return text;
}

/**
 * The one key every form of a code shares: UPC-A, UPC-E and EAN-13 give the GTIN-13, EAN-8 stays 8 digits, and ISBN-10
 * and ISBN-13 give the ISBN-13 (which is also the book's GTIN-13). Null when the text is not a valid code.
 */
export function normalizeCode(input: string | null | undefined): string | null {
  const text = clean(input);
  if (text === null) return null;
  return parseGtin(text) ?? parseIsbn13(text);
}
