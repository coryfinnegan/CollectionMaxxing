// The Lottie moments are hand-drawn JSON: every file must parse to a valid animation
// with frames to play and layers to draw.
import emptyLibrary from '@/assets/lottie/empty-library.json';
import noResults from '@/assets/lottie/no-results.json';
import successCheck from '@/assets/lottie/success-check.json';

const spots = { 'success-check': successCheck, 'empty-library': emptyLibrary, 'no-results': noResults };

describe('lottie spots', () => {
  for (const [name, spot] of Object.entries(spots)) {
    it(`${name} is a valid animation`, () => {
      expect(typeof spot.v).toBe('string');
      expect(spot.fr).toBeGreaterThan(0);
      expect(spot.op).toBeGreaterThan(spot.ip);
      expect(spot.w).toBeGreaterThan(0);
      expect(spot.h).toBeGreaterThan(0);
      expect(Array.isArray(spot.assets)).toBe(true);
      expect(spot.layers.length).toBeGreaterThan(0);
      for (const layer of spot.layers) {
        expect(layer.shapes.length).toBeGreaterThan(0);
      }
    });
  }

  it('the success check plays once in about a second', () => {
    expect((successCheck.op - successCheck.ip) / successCheck.fr).toBeLessThanOrEqual(1.5);
  });

  it('the empty states loop seamlessly (last frame matches the first)', () => {
    for (const spot of [emptyLibrary, noResults]) {
      for (const layer of spot.layers) {
        for (const channel of Object.values(layer.ks)) {
          const keys = (channel as { a: number; k: unknown }).k;
          if (!Array.isArray(keys) || typeof keys[0] !== 'object') continue;
          const first = keys[0] as { s: unknown };
          const last = keys[keys.length - 1] as { s: unknown; t: number };
          expect({ at: last.t, value: last.s }).toEqual({ at: spot.op, value: first.s });
        }
      }
    }
  });
});
