// Cover-derived header colors (#306): tone the cover down to a dark band and
// prove the text on it reaches WCAG AA, or say so (null) and the screen falls
// back to the Phase 0 tokens.
import { contrastRatio } from '@/theme/contrast';
import { darken, desaturate, headerThemeFromCover, parseHex, pickSource, toHex } from './coverColor';

describe('parseHex and toHex', () => {
  it('round-trips a hex color, with or without the hash', () => {
    expect(toHex(parseHex('#e11d48')!)).toBe('#e11d48');
    expect(toHex(parseHex('e11d48')!)).toBe('#e11d48');
  });

  it('rejects what is not a six-digit hex color', () => {
    expect(parseHex('')).toBeNull();
    expect(parseHex('red')).toBeNull();
    expect(parseHex('#fff')).toBeNull();
    expect(parseHex('#gggggg')).toBeNull();
  });
});

describe('desaturate and darken', () => {
  it('leaves the color alone at 0 and flattens it at 1', () => {
    expect(desaturate('#e11d48', 0)).toBe('#e11d48');
    expect(darken('#e11d48', 0)).toBe('#e11d48');
    expect(desaturate('#e11d48', 1)).toBe('#6d6d6d');
    expect(darken('#e11d48', 1)).toBe('#000000');
  });

  it('rejects what is not a color', () => {
    expect(desaturate('nope', 0.5)).toBeNull();
    expect(darken('nope', 0.5)).toBeNull();
  });
});

describe('pickSource', () => {
  it('takes the muted tone on each platform', () => {
    expect(pickSource({ background: '#111111', primary: '#222222', secondary: '#333333', detail: '#444444', platform: 'ios', quality: 'high' })).toBe('#111111');
    expect(
      pickSource({ dominant: '#111111', vibrant: '#222222', darkVibrant: '#333333', lightVibrant: '#444444', darkMuted: '#555555', lightMuted: '#666666', muted: '#777777', platform: 'web' }),
    ).toBe('#777777');
    expect(
      pickSource({ dominant: '#111111', average: '#222222', vibrant: '#333333', darkVibrant: '#444444', lightVibrant: '#555555', darkMuted: '#666666', lightMuted: '#777777', muted: '#888888', platform: 'android' }),
    ).toBe('#111111');
  });

  it('falls to the next tone when the first is missing', () => {
    expect(pickSource({ background: '', primary: '#222222', secondary: '', detail: '', platform: 'ios', quality: 'high' })).toBe('#222222');
    expect(
      pickSource({ dominant: '#111111', vibrant: '', darkVibrant: '', lightVibrant: '', darkMuted: '', lightMuted: '', muted: '', platform: 'web' }),
    ).toBe('#111111');
  });
});

describe('headerThemeFromCover', () => {
  it('turns a vivid cover into a dark band with light text that passes AA', () => {
    for (const cover of ['#e11d48', '#2563eb', '#16a34a', '#eab308', '#f8fafc', '#18181b']) {
      const theme = headerThemeFromCover(cover, '#ffffff', '#1c1917');
      expect(theme).not.toBeNull();
      expect(contrastRatio(theme!.background, theme!.text)).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('prefers the dark text when it also passes on a deep band', () => {
    // A near-black band passes white, so this pins the light-first order, not the tone.
    expect(headerThemeFromCover('#18181b', '#ffffff', '#1c1917')!.text).toBe('#ffffff');
  });

  it('falls back when neither candidate reaches 4.5 to 1', () => {
    expect(headerThemeFromCover('#ffffff', '#777777', '#888888')).toBeNull();
  });

  it('falls back when the cover is not a color', () => {
    expect(headerThemeFromCover('nope', '#ffffff', '#1c1917')).toBeNull();
  });
});
