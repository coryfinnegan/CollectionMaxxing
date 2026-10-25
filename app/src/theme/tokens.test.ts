// The theme derives from tokens.json: both schemes resolve the same roles to real colors,
// so a typo in a role reference fails here instead of painting `undefined`.
import { darkColors, fixed, lightColors, typeScale } from './tokens';
import source from './tokens.json';

const hex = /^#[0-9a-fA-F]{6}$/;

describe('design tokens', () => {
  it('resolves the same roles in both schemes', () => {
    expect(Object.keys(lightColors).sort()).toEqual(Object.keys(darkColors).sort());
  });

  it.each(Object.entries(lightColors))('resolves light %s to a hex color', (_role, value) => {
    expect(value).toMatch(hex);
  });

  it.each(Object.entries(darkColors))('resolves dark %s to a hex color', (_role, value) => {
    expect(value).toMatch(hex);
  });

  it('keeps every scale at 12 hex steps', () => {
    for (const steps of Object.values(source.scales)) {
      expect(steps).toHaveLength(12);
      for (const step of steps as string[]) expect(step).toMatch(hex);
    }
  });

  it('keeps the fixed values scheme-independent', () => {
    expect(fixed.black).toBe('#000000');
    expect(fixed.white).toBe('#FFFFFF');
    expect(fixed.scrim).toBe('rgba(0,0,0,0.5)');
  });

  it('offers the semantic type scale, UI faces plus mono metadata', () => {
    for (const variant of ['display', 'title', 'heading', 'body', 'label', 'caption', 'mono']) {
      expect(typeScale[variant].fontFamily).toMatch(/^(BricolageGrotesque|SpaceMono)_/);
    }
    expect(typeScale.mono.fontFamily).toMatch(/^SpaceMono_/);
    expect(typeScale.display.fontSize ?? 0).toBeGreaterThan(typeScale.title.fontSize ?? 0);
  });
});
