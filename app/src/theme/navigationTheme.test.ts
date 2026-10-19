import { navigationThemeFor } from './navigationTheme';
import { darkColors, lightColors } from './tokens';

describe('navigation theme', () => {
  it('paints screens with the ground colour and chrome with the surface colour in light mode', () => {
    const theme = navigationThemeFor(false);
    expect(theme.dark).toBe(false);
    expect(theme.colors.background).toBe(lightColors.ground);
    expect(theme.colors.card).toBe(lightColors.surface);
    expect(theme.colors.text).toBe(lightColors.ink);
    expect(theme.colors.border).toBe(lightColors.line);
  });

  it('paints screens with the ground colour and chrome with the surface colour in dark mode', () => {
    const theme = navigationThemeFor(true);
    expect(theme.dark).toBe(true);
    expect(theme.colors.background).toBe(darkColors.ground);
    expect(theme.colors.card).toBe(darkColors.surface);
    expect(theme.colors.text).toBe(darkColors.ink);
    expect(theme.colors.primary).toBe(darkColors.scan);
  });
});
