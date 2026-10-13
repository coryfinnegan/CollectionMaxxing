import { mapScannedObjects } from '../mapScannedObjects';

const box = { x: 0.1, y: 0.2, width: 0.3, height: 0.4 };

describe('mapScannedObjects', () => {
  it('maps the five supported symbologies to DetectedCode', () => {
    const codes = mapScannedObjects([
      { type: 'ean-13', value: '9780441172719', boundingBox: box },
      { type: 'ean-8', value: '96385074', boundingBox: box },
      { type: 'upc-e', value: '01234565', boundingBox: box },
      { type: 'qr', value: 'hello', boundingBox: box },
      { type: 'code-128', value: 'ABC', boundingBox: box },
    ]);
    expect(codes.map((c) => c.symbology)).toEqual(['ean-13', 'ean-8', 'upc-e', 'qr', 'code-128']);
    expect(codes[0]).toEqual({ value: '9780441172719', symbology: 'ean-13', bounds: box });
  });

  it('drops faces, other symbologies and codes with no value', () => {
    expect(
      mapScannedObjects([
        { type: 'face', boundingBox: box },
        { type: 'pdf-417', value: 'x', boundingBox: box },
        { type: 'qr', value: undefined, boundingBox: box },
        { type: 'qr', value: '', boundingBox: box },
      ]),
    ).toEqual([]);
  });
});
