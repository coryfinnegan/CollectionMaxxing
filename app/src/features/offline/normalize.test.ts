// The same vectors as CollectionMaxxing.Domain.Tests (GtinTests, IsbnTests): the phone and the server must agree on what a code is.
import { normalizeCode } from './normalize';

describe('normalizeCode', () => {
  it.each([
    ['036000291452', '0036000291452'],
    ['012345678905', '0012345678905'],
    ['042100005264', '0042100005264'],
    ['072030131122', '0072030131122'],
  ])('reads UPC-A %s as GTIN-13', (upcA, expected) => {
    expect(normalizeCode(upcA)).toBe(expected);
  });

  it.each([
    ['036000291453'],
    ['012345678906'],
    ['4006381333932'],
    ['5901234123458'],
    ['73513538'],
    ['abc'],
    [''],
    ['   '],
    ['978'],
    ['97803064061'],
  ])('rejects %j', (bad) => {
    expect(normalizeCode(bad)).toBeNull();
  });

  it.each([null, undefined])('rejects %s', (bad) => {
    expect(normalizeCode(bad)).toBeNull();
  });

  it.each([
    ['04252614', '0042100005264'],
    ['01234505', '0012000003455'],
    ['01234523', '0012200003453'],
    ['01234531', '0012300000451'],
    ['01234543', '0012340000053'],
    ['01234565', '0012345000065'],
    ['01234596', '0012345000096'],
    ['11234502', '0112000003452'],
    ['11234538', '0112300000458'],
    ['16543270', '0165432000070'],
    ['12345687', '0123456000087'],
  ])('expands UPC-E %s to the same key as its UPC-A', (upcE, expected) => {
    expect(normalizeCode(upcE)).toBe(expected);
  });

  it.each([['04252615'], ['01234506'], ['94252614']])('rejects the bad UPC-E %s', (bad) => {
    expect(normalizeCode(bad)).toBeNull();
  });

  it.each([['4006381333931'], ['5901234123457'], ['5012345678900'], ['4000000000006'], ['3017620422003']])(
    'keeps EAN-13 %s as is',
    (ean) => {
      expect(normalizeCode(ean)).toBe(ean);
    },
  );

  it.each([['73513537'], ['96385074'], ['55123457']])('keeps EAN-8 %s as is', (ean8) => {
    expect(normalizeCode(ean8)).toBe(ean8);
  });

  it('reads an eight digit code valid both ways as UPC-E, like the server', () => {
    expect(normalizeCode('12345670')).toBe('0123456000070');
  });

  it('gives the UPC-A, UPC-E and EAN-13 forms of one item the same key', () => {
    const key = normalizeCode('042100005264');
    expect(normalizeCode('0042100005264')).toBe(key);
    expect(normalizeCode('04252614')).toBe(key);
    expect(normalizeCode('0-42100-00526-4')).toBe(key);
  });

  it.each([
    ['9780306406157'],
    ['978-0-306-40615-7'],
    [' 978 0 306 40615 7 '],
    ['ISBN 978-0-306-40615-7'],
    ['ISBN-13: 978-0-306-40615-7'],
    ['9780306406157 12345'],
    ['978030640615712345'],
    ['978030640615734'],
    ['9780306406157 34'],
    ['0306406152'],
    ['0-306-40615-2'],
    ['ISBN 0-306-40615-2'],
  ])('gives every form of the same book (%j) one key', (typed) => {
    expect(normalizeCode(typed)).toBe('9780306406157');
  });

  it.each([
    ['080442957X', '9780804429573'],
    ['080442957x', '9780804429573'],
    ['0451524934', '9780451524935'],
    ['0316062642', '9780316062640'],
  ])('converts ISBN-10 %s to ISBN-13', (isbn10, isbn13) => {
    expect(normalizeCode(isbn10)).toBe(isbn13);
  });

  it.each([['0306406153'], ['0804429570'], ['030640615X']])('rejects the bad ISBN-10 %s', (bad) => {
    expect(normalizeCode(bad)).toBeNull();
  });

  it('reads 979-10 and 979-8 ISBN-13s', () => {
    expect(normalizeCode('9791000000015')).toBe('9791000000015');
    expect(normalizeCode('9798265172143')).toBe('9798265172143');
  });

  it('reads a GTIN-14 with indicator 0 as its GTIN-13 and rejects other indicators', () => {
    expect(normalizeCode('04006381333931')).toBe('4006381333931');
    expect(normalizeCode('14006381333938')).toBeNull();
  });

  it('tolerates scanner add-ons on UPC-A', () => {
    expect(normalizeCode('036000291452 12345')).toBe('0036000291452');
    expect(normalizeCode('03600029145254321')).toBe('0036000291452');
  });
});
