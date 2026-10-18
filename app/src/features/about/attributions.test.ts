import { ATTRIBUTIONS } from './attributions';

describe('provider attributions', () => {
  it('has exactly the providers the server uses', () => {
    expect(ATTRIBUTIONS.map((a) => a.name).sort()).toEqual(['Google Books', 'IGDB', 'Open Library', 'TMDB', 'UPCitemdb']);
  });

  it('gives each a notice and an https link', () => {
    for (const a of ATTRIBUTIONS) {
      expect(a.notice.trim().length).toBeGreaterThan(10);
      expect(a.url).toMatch(/^https:\/\/[^\s]+$/);
    }
  });

  it('uses text credits only, with no logo files', () => {
    for (const a of ATTRIBUTIONS) expect(Object.keys(a)).not.toContain('logo');
  });

  it('carries the notice TMDB requires', () => {
    const tmdb = ATTRIBUTIONS.find((a) => a.name === 'TMDB');
    expect(tmdb?.notice).toContain('not endorsed or certified by TMDB');
  });
});
