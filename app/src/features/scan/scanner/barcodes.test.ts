import { isReadableBarcode } from './barcodes';

describe('isReadableBarcode', () => {
  it.each([
    ['the Hyrule barcode from #383', '0045496905934'],
    ['a UPC-A', '045496905934'],
    ['an EAN-13', '4006381333931'],
    ['a book ISBN-13', '9780441172719'],
    ['an ISBN-10', '0306406152'],
    ['an ISBN-10 with an X check', '097522980X'],
    ['a UPC-E', '01234565'],
    ['an EAN-8', '04210009'],
    ['a GTIN-14 with indicator 0', '00454969059348'],
    ['hyphens and spaces tolerated', '978-0-441-17271-9'],
    ['an ISBN label tolerated', 'ISBN: 9780441172719'],
    ['a 5 digit add-on tolerated', '9780441172719 51295'],
  ])('accepts %s (%s)', (_label, value) => {
    expect(isReadableBarcode(value)).toBe(true);
  });

  it.each([
    ['an 11 digit partial of a UPC-A', '04549690593'],
    ['a 9 digit fragment', '004549690'],
    ['a 7 digit fragment', '0045496'],
    ['a full-length code with a wrong check digit', '9780441172718'],
    ['a GTIN-14 naming a carton (indicator 1)', '10454969059348'],
    ['a QR payload', 'https://example.com/s/123'],
    ['a test placeholder', 'code-1'],
    ['nothing', ''],
    ['blanks', '   '],
  ])('rejects %s (%s)', (_label, value) => {
    expect(isReadableBarcode(value)).toBe(false);
  });

  it('accepts a check-valid EAN-8 even when it is really a misdecode, like 97127666 in #383', () => {
    // Nothing at capture can tell a check-valid misdecode from a real EAN-8; the lookup sorts it out.
    expect(isReadableBarcode('97127666')).toBe(true);
  });
});
