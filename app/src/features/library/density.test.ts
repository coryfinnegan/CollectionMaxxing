// The castle decoration density (#357): the theme thins as the shelf fills.
import { castleDensityForCount } from './density';

describe('castleDensityForCount', () => {
  it('shows the full theme for a bare shelf', () => {
    expect(castleDensityForCount(0)).toBe('full');
    expect(castleDensityForCount(1)).toBe('full');
  });

  it('keeps the frame for a handful of items', () => {
    expect(castleDensityForCount(2)).toBe('frame');
    expect(castleDensityForCount(12)).toBe('frame');
  });

  it('drops to covers and the header once the shelf fills', () => {
    expect(castleDensityForCount(13)).toBe('covers');
    expect(castleDensityForCount(500)).toBe('covers');
  });
});
