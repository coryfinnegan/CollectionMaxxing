import { contrastRatio } from './contrast';
import { type Colors, darkColors, lightColors } from './tokens';

type Pair = { fg: keyof Colors; bg: keyof Colors; use: string };

// Text must reach 4.5:1 (WCAG 1.4.3); the edges of controls and icons must reach 3:1 (1.4.11).
const text: Pair[] = [
  { fg: 'ink', bg: 'ground', use: 'body text on the page' },
  { fg: 'ink', bg: 'surface', use: 'body text on a card' },
  { fg: 'muted', bg: 'ground', use: 'secondary text on the page' },
  { fg: 'muted', bg: 'surface', use: 'secondary text on a card' },
  { fg: 'scan', bg: 'ground', use: 'link text on the page' },
  { fg: 'scan', bg: 'surface', use: 'link text on a card' },
  { fg: 'onScan', bg: 'scan', use: 'primary button label' },
  { fg: 'danger', bg: 'ground', use: 'error text on the page' },
  { fg: 'danger', bg: 'surface', use: 'error text on a card' },
  { fg: 'stickerText', bg: 'ground', use: 'sticker text on the page' },
  { fg: 'stickerText', bg: 'surface', use: 'sticker text on a card' },
  { fg: 'ground', bg: 'ink', use: 'snackbar text' },
];
const controls: Pair[] = [
  { fg: 'control', bg: 'surface', use: 'field and button borders on a card' },
  { fg: 'control', bg: 'ground', use: 'field and button borders on the page' },
  { fg: 'scan', bg: 'surface', use: 'selected chip and primary button edge' },
  { fg: 'scan', bg: 'ground', use: 'primary button edge on the page' },
  { fg: 'danger', bg: 'surface', use: 'danger button edge' },
  { fg: 'danger', bg: 'ground', use: 'danger button edge on the page' },
];

describe.each([
  ['light', lightColors],
  ['dark', darkColors],
] as const)('%s palette contrast', (_name, colors) => {
  it.each(text)('text: $use ($fg on $bg) is at least 4.5:1', ({ fg, bg }) => {
    expect(contrastRatio(colors[fg], colors[bg])).toBeGreaterThanOrEqual(4.5);
  });
  it.each(controls)('controls: $use ($fg on $bg) is at least 3:1', ({ fg, bg }) => {
    expect(contrastRatio(colors[fg], colors[bg])).toBeGreaterThanOrEqual(3);
  });
});

// The receipt is paper in both themes (receipt text is fixed ink on a light ground).
describe('receipt paper contrast', () => {
  it.each([
    ['#1A1C22', '#FBFBF7'],
    ['#5E6472', '#FBFBF7'],
    [lightColors.scan, lightColors.receipt],
  ])('%s on %s is at least 4.5:1', (fg, bg) => {
    expect(contrastRatio(fg, bg)).toBeGreaterThanOrEqual(4.5);
  });
});
