import { gridColumns, tileWidthFor } from './gridLayout';

describe('gridColumns', () => {
  it.each([
    [375, 3],
    [428, 3],
    // The web sidebar takes 220 px, so a 768 px window leaves a 548 px grid.
    [548, 3],
    [800, 5],
    // A 1280 px window minus the sidebar: the grid is 1060 px wide and fits six tiles.
    [1060, 6],
    [2400, 6],
  ])('picks the column count from a %s px wide grid', (width, expected) => {
    expect(gridColumns(width)).toBe(expected);
  });

  it('never goes below three columns, even in a very narrow grid', () => {
    expect(gridColumns(200)).toBe(3);
    expect(gridColumns(0)).toBe(3);
  });
});

describe('tileWidthFor', () => {
  it('fits every column inside the grid width, padding and gaps included', () => {
    for (const width of [375, 548, 800, 1060, 1920]) {
      const columns = gridColumns(width);
      const tile = tileWidthFor(width, columns);
      // 16 px padding on each side and 12 px between tiles.
      expect(tile * columns + 12 * (columns - 1) + 32).toBeLessThanOrEqual(width);
    }
  });

  it('keeps the phone tile width it always had', () => {
    expect(tileWidthFor(375, gridColumns(375))).toBe(106);
  });
});
