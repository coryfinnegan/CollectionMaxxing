// The "scan these 6 codes" set for the dev menu and the mock API. Fake barcodes only; they match entries in
// app/src/mocks/scan.ts, so every kind of answer shows up: owned, new, owned and lent out, not found,
// slow to resolve, and a code the catalog cannot read.
export const DEV_CODES = {
  duneOwned: '9780441172719',
  mario64New: '0045496870010',
  ghostbustersNew: '9786000000011',
  goldeneyeOwned: '0045496870027',
  unknown: '0000000000000',
  slow: '4006381333931',
  unreadable: '9999999999999',
  // In the mock library only, and not in the dev menu's six: one on the wishlist, one sold, one owned but marked missing.
  wishlisted: '9786000000028',
  sold: '9786000000035',
  missing: '9786000000042',
} as const;

export const DEV_SIX_CODES: { value: string; symbology: string }[] = [
  DEV_CODES.duneOwned,
  DEV_CODES.mario64New,
  DEV_CODES.ghostbustersNew,
  DEV_CODES.goldeneyeOwned,
  DEV_CODES.unknown,
  DEV_CODES.slow,
].map((value) => ({ value, symbology: 'ean-13' }));
